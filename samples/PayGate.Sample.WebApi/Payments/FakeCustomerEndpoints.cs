namespace PayGate.Sample.WebApi.Payments;

using PayGate.Testing;
using PayGate.Webhooks;

/// <summary>
/// Development only: plays the customer and the provider for the fake gateway. Each call changes the fake
/// payment, then delivers its signed webhook through the same processor that <c>/webhooks/fake</c> uses.
/// </summary>
public static class FakeCustomerEndpoints
{
    public static IEndpointRouteBuilder MapFakeCustomerEndpoints(this IEndpointRouteBuilder endpoints, PaymentAccount account)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/dev/fake/payments/{providerPaymentId}");
        group.MapPost("/succeed", (string providerPaymentId, IServiceProvider services, CancellationToken cancellationToken) =>
            MoveAsync(services, account, providerPaymentId, fake => fake.Succeed(providerPaymentId), cancellationToken));
        group.MapPost("/fail", (string providerPaymentId, IServiceProvider services, CancellationToken cancellationToken) =>
            MoveAsync(services, account, providerPaymentId, fake => fake.Fail(providerPaymentId), cancellationToken));
        group.MapPost("/expire", (string providerPaymentId, IServiceProvider services, CancellationToken cancellationToken) =>
            MoveAsync(services, account, providerPaymentId, fake => fake.Expire(providerPaymentId), cancellationToken));

        // Delivers the current state again without changing it: what a provider's retry looks like.
        group.MapPost("/webhook", (string providerPaymentId, IServiceProvider services, CancellationToken cancellationToken) =>
            MoveAsync(services, account, providerPaymentId, _ => { }, cancellationToken));
        return endpoints;
    }

    private static async Task<IResult> MoveAsync(
        IServiceProvider services,
        PaymentAccount account,
        string providerPaymentId,
        Action<FakePaymentGateway> move,
        CancellationToken cancellationToken)
    {
        var fake = services.GetRequiredKeyedService<FakePaymentGateway>(account.ToString());
        try
        {
            move(fake);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }

        var webhook = new FakeWebhookBuilder(fake).ForPayment(providerPaymentId);
        var result = await services.GetRequiredService<IWebhookProcessor>().ProcessAsync(webhook, cancellationToken);
        return Results.Ok(new { webhook = result.Status.ToString(), statusCode = result.Ack.StatusCode, eventIds = result.EventIds });
    }
}
