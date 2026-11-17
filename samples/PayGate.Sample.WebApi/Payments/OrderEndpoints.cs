namespace PayGate.Sample.WebApi.Payments;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayGate.AspNetCore;
using PayGate.Sample.WebApi.Data;

/// <summary>The shop's own endpoints. Everything here is app code: PayGate only provides the gateway calls.</summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/orders", CreateOrderAsync);
        endpoints.MapGet("/orders/{id:int}", GetOrderAsync);
        endpoints.MapPost("/orders/{id:int}/checkout", CheckoutAsync);
        endpoints.MapPost("/orders/{id:int}/refunds", RefundAsync);
        endpoints.MapGet("/payments/return", ReturnAsync);
        return endpoints;
    }

    /// <summary>The webhook address for an account, for providers that take one per payment.</summary>
    public static Uri WebhookUrl(SamplePaymentOptions settings, PaymentAccount account)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string path = $"{settings.WebhookPrefix.Trim('/')}/{account.Provider.Value}";
        return new Uri(settings.PublicBaseUrl, account.IsDefault ? path : $"{path}/{account.Name}");
    }

    private static async Task<IResult> CreateOrderAsync(CreateOrderRequest request, SampleDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || !Currency.TryFromCode(request.Currency, out var currency))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A description and a known currency are required.");
        }

        Money amount;
        try
        {
            amount = new Money(request.Amount, currency);
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The amount is not valid.", detail: exception.Message);
        }

        if (amount.IsZero)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The amount must be positive.");
        }

        var order = new Order
        {
            Description = request.Description,
            AmountMinor = amount.ToMinorUnits(),
            Currency = currency.Code,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/orders/{order.Id}", OrderResponse.From(order));
    }

    private static async Task<IResult> GetOrderAsync(int id, SampleDbContext db, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Attempts).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order));
    }

    /// <summary>Each press of "Pay" is a new attempt with a new reference, never a retry of the old one.</summary>
    private static async Task<IResult> CheckoutAsync(
        int id,
        CheckoutRequest? request,
        SampleDbContext db,
        IPaymentGatewayResolver gateways,
        IPayGateReturnCallbacks returns,
        IOptions<SamplePaymentOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Attempts).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (order.Status != OrderStatus.AwaitingPayment)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"The order is {order.Status}.");
        }

        // Checked now, so that the customer is not sent to pay and then stranded on an unsafe address.
        Uri? next = null;
        if (request?.Next is { } candidate && !returns.TryGetSafeReturnUrl(candidate, out next))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The next address is not allowed.");
        }

        var settings = options.Value;
        var account = PaymentAccount.Parse(settings.Account);
        var gateway = gateways.GetGateway(account);
        var now = timeProvider.GetUtcNow();
        int number = order.Attempts.Count == 0 ? 1 : order.Attempts.Max(a => a.Number) + 1;
        var attempt = new PaymentAttempt
        {
            Order = order,
            Number = number,
            MerchantReference = string.Create(CultureInfo.InvariantCulture, $"ORD-{order.Id}-R{number}"),
            Account = account.ToString(),
            AmountMinor = order.AmountMinor,
            Currency = order.Currency,
            CreatedAt = now,
            UpdatedAt = now,
        };
        order.Attempts.Add(attempt);

        // Saved before calling the provider: its webhook can arrive before the call returns, and must find the attempt.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SampleDbContext.IsUniqueViolation(exception))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Another checkout for this order started at the same time. Try again.");
        }

        var result = await gateway.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                MerchantReference = attempt.MerchantReference,
                Amount = order.Amount,
                Description = order.Description,
                ReturnUrl = ReturnUrl(settings, attempt.MerchantReference, next),
                NotificationUrl = gateway.Capabilities.PerRequestNotificationUrl ? WebhookUrl(settings, account) : null,
                IdempotencyKey = attempt.MerchantReference,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["orderId"] = order.Id.ToString(CultureInfo.InvariantCulture),
                },
            },
            cancellationToken);

        if (result.IsFailure)
        {
            if (result.Error.Code == PaymentErrorCode.OutcomeUnknown)
            {
                // The provider may or may not have it. Reconciliation looks it up by reference later.
                attempt.LastError = "Creating the payment timed out.";
                await db.SaveChangesAsync(cancellationToken);
                return Results.Accepted(
                    $"/orders/{order.Id}",
                    new PendingResponse(attempt.MerchantReference, "The provider did not answer in time. Check the order again in a few minutes."));
            }

            attempt.Status = PaymentStatus.Failed;
            attempt.LastError = $"Creating the payment failed with {result.Error.Code}.";
            await db.SaveChangesAsync(cancellationToken);
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The provider refused the payment.", detail: attempt.LastError);
        }

        var session = result.Value;
        attempt.ProviderPaymentId = session.ProviderPaymentId;
        if (attempt.Status.CanTransitionTo(session.Status))
        {
            attempt.Status = session.Status;
        }

        attempt.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(CheckoutResponse.From(attempt, session));
    }

    /// <summary>
    /// Where the provider sends the customer back. The query string comes from the customer's browser, so it
    /// proves nothing: the page asks the provider itself before showing "paid".
    /// </summary>
    private static async Task<IResult> ReturnAsync(
        string? reference,
        string? next,
        SampleDbContext db,
        PaymentUpdater updater,
        IPaymentGatewayResolver gateways,
        IPayGateReturnCallbacks returns,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The reference is missing.");
        }

        var attempt = await db.PaymentAttempts.Include(a => a.Order).FirstOrDefaultAsync(a => a.MerchantReference == reference, cancellationToken);
        if (attempt is null)
        {
            return Results.NotFound();
        }

        if (gateways.TryGetGateway(attempt.PaymentAccount, out var gateway))
        {
            var lookup = attempt.ProviderPaymentId is { } providerPaymentId
                ? PaymentLookup.ByProviderPaymentId(providerPaymentId)
                : gateway.Capabilities.LookupByMerchantReference ? PaymentLookup.ByMerchantReference(attempt.MerchantReference) : null;

            // If the provider cannot be reached, the page shows what the app knows; the webhook or reconciliation catches up.
            var details = lookup is null ? null : await gateway.GetPaymentAsync(lookup, cancellationToken);
            if (details is { IsSuccess: true })
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await updater.ApplyAsync(attempt, PaymentObservation.FromDetails(details.Value, "return page"), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        // Checked again: the query string could have been edited since checkout.
        return returns.TryGetSafeReturnUrl(next, out var url)
            ? Results.Redirect(url.ToString())
            : Results.Ok(new ReturnResponse(attempt.OrderId, attempt.MerchantReference, attempt.Status, attempt.Order.Status));
    }

    /// <summary>Partial or full. Each refund gets its own reference, so that a retried call is not refunded twice.</summary>
    private static async Task<IResult> RefundAsync(
        int id,
        RefundOrderRequest? request,
        SampleDbContext db,
        PaymentUpdater updater,
        IPaymentGatewayResolver gateways,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(o => o.Attempts).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Results.NotFound();
        }

        var attempt = order.Attempts.Find(a => a.Id == order.PaidAttemptId);
        if (attempt?.ProviderPaymentId is null || attempt.Status is not (PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The order has no payment left to refund.");
        }

        var remaining = attempt.Amount - attempt.Refunded;
        Money amount;
        try
        {
            amount = request?.Amount is { } value ? new Money(value, remaining.Currency) : remaining;
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The amount is not valid.", detail: exception.Message);
        }

        if (amount.IsZero || amount > remaining)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"The refund must be positive and at most {remaining}.");
        }

        if (!gateways.TryGetGateway(attempt.PaymentAccount, out var gateway))
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The payment's account is not configured.");
        }

        if (amount != remaining && !gateway.Capabilities.PartialRefund)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "This provider only refunds the whole payment.");
        }

        // Reserve the reference before calling, so that the next refund never reuses it.
        attempt.RefundRequests++;
        string refundReference = string.Create(CultureInfo.InvariantCulture, $"{attempt.MerchantReference}-F{attempt.RefundRequests}");
        await db.SaveChangesAsync(cancellationToken);

        var result = await gateway.RefundAsync(
            new RefundRequest
            {
                ProviderPaymentId = attempt.ProviderPaymentId,
                ProviderTransactionId = attempt.ProviderTransactionId,
                Amount = amount,
                Reason = request?.Reason,
                MerchantRefundReference = refundReference,
            },
            cancellationToken);

        if (result.IsFailure)
        {
            if (result.Error.Code == PaymentErrorCode.OutcomeUnknown)
            {
                return Results.Accepted($"/orders/{order.Id}", new PendingResponse(refundReference, "The provider did not answer in time. Its webhook will tell."));
            }

            attempt.LastError = $"Refund {refundReference} failed with {result.Error.Code}.";
            await db.SaveChangesAsync(cancellationToken);
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The provider refused the refund.", detail: attempt.LastError);
        }

        var refund = result.Value;
        if (refund.Status == RefundStatus.Succeeded)
        {
            var total = attempt.Refunded + (refund.Amount ?? amount);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await updater.ApplyAsync(
                attempt,
                new PaymentObservation
                {
                    Status = total == attempt.Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded,
                    RefundedTotal = total,
                    Source = "refund",
                },
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // A pending refund is confirmed later by the provider's webhook, which reports the new refunded total.
        var response = new RefundResponse(refundReference, refund.Status, attempt.Refunded.Amount, attempt.Status);
        return refund.Status == RefundStatus.Failed
            ? Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The provider declined the refund.")
            : refund.Status == RefundStatus.Pending ? Results.Accepted($"/orders/{order.Id}", response) : Results.Ok(response);
    }

    private static Uri ReturnUrl(SamplePaymentOptions settings, string reference, Uri? next)
    {
        string query = $"payments/return?reference={Uri.EscapeDataString(reference)}";
        return new Uri(settings.PublicBaseUrl, next is null ? query : $"{query}&next={Uri.EscapeDataString(next.OriginalString)}");
    }
}
