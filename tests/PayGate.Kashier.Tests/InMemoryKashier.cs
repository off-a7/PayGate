namespace PayGate.Kashier.Tests;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PayGate.Webhooks;

/// <summary>
/// A stateful stand-in for Kashier's two hosts, answering in the shapes recorded in tests/fixtures/kashier/api. It knows
/// sessions, the order a payment opens, order search, refunds and voids, so the conformance suite can drive a whole payment.
/// </summary>
internal sealed class InMemoryKashier : HttpMessageHandler
{
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Lock _lock = new();
    private int _next;
    private bool _hangNextWrite;

    /// <summary>Every POST and PUT that reached Kashier, oldest first. Kashier has no idempotency key, so each key is null.</summary>
    public List<string?> WriteAttempts { get; } = [];

    public void Pay(string sessionId)
    {
        lock (_lock)
        {
            var session = _sessions[sessionId];
            session.Status = "PAID";
            session.OrderId = $"order-{Next()}";
            session.TransactionId = $"TX-00000000{Next()}";
            session.Captured = session.Amount;
        }
    }

    public void ReportStatus(string sessionId, string rawStatus)
    {
        lock (_lock)
        {
            _sessions[sessionId].Status = rawStatus;
        }
    }

    public void HangNextWrite()
    {
        lock (_lock)
        {
            _hangNextWrite = true;
        }
    }

    /// <summary>The <c>pay</c> / SUCCESS webhook Kashier sends for a paid session, signed the way Kashier signs it.</summary>
    public WebhookRequest PaidWebhook(string sessionId, string key)
    {
        Session session;
        lock (_lock)
        {
            session = _sessions[sessionId];
        }

        var data = new JsonObject
        {
            ["merchantOrderId"] = session.Reference,
            ["kashierOrderId"] = session.OrderId,
            ["orderReference"] = $"TEST-ORD-{sessionId[..6]}",
            ["transactionId"] = session.TransactionId,
            ["status"] = "SUCCESS",
            ["method"] = "card",
            ["creationDate"] = "2026-11-24T09:00:00.000Z",
            ["amount"] = JsonNode.Parse(session.Amount.ToString(CultureInfo.InvariantCulture)),
            ["currency"] = session.Currency,
            ["transactionResponseCode"] = "00",
            ["channel"] = "online | e-commerce",
            ["signatureKeys"] = new JsonArray("amount", "channel", "currency", "kashierOrderId", "merchantOrderId", "method", "orderReference", "status", "transactionId", "transactionResponseCode"),
        };
        var body = new JsonObject { ["platform"] = "kashier", ["event"] = "pay", ["data"] = data };
        return Fixtures.Webhook(body, Fixtures.SignWebhook(data, key));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        string path = request.RequestUri!.AbsolutePath.Trim('/');
        bool isWrite = request.Method == HttpMethod.Post || request.Method == HttpMethod.Put;

        bool hang;
        lock (_lock)
        {
            if (isWrite)
            {
                WriteAttempts.Add(null);
            }

            hang = isWrite && _hangNextWrite;
            _hangNextWrite &= !hang;
        }

        if (hang)
        {
            // The write reached Kashier and its answer was lost.
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        lock (_lock)
        {
            return (request.Method.Method, path.Split('/')) switch
            {
                ("POST", ["v3", "payment", "sessions"]) => Create(JsonNode.Parse(body!)!),
                ("GET", ["v3", "payment", "sessions", var id, "payment"]) => Read(id),
                ("GET", ["v3", "payment", "orders"]) => Search(Uri.UnescapeDataString(request.RequestUri.Query.Replace("?search=", string.Empty, StringComparison.Ordinal))),
                ("PUT", ["v3", "orders", var orderId]) => Operate(orderId, JsonNode.Parse(body!)!),
                _ => Answer(HttpStatusCode.NotFound, new JsonObject { ["message"] = $"No route {request.Method} /{path}" }),
            };
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, JsonObject body) => new(status)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private HttpResponseMessage Create(JsonNode request)
    {
        string id = $"6b0{Next():D21}";
        _sessions[id] = new Session
        {
            Id = id,
            Reference = request["order"]!.GetValue<string>(),
            Amount = decimal.Parse(request["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            Currency = request["currency"]!.GetValue<string>(),
        };
        return Answer(HttpStatusCode.OK, new JsonObject
        {
            ["_id"] = id,
            ["status"] = "CREATED",
            ["sessionUrl"] = $"https://payments.kashier.io/session/{id}?mode=test",
        });
    }

    private HttpResponseMessage Read(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return Answer(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Session not found" });
        }

        return Answer(HttpStatusCode.OK, new JsonObject
        {
            ["message"] = "success",
            ["data"] = new JsonObject
            {
                ["sessionId"] = session.Id,
                ["status"] = session.Status,
                ["merchantOrderId"] = session.Reference,
                ["amount"] = session.Amount,
                ["currency"] = session.Currency,
                ["method"] = session.OrderId is null ? "NA" : "card",
                ["orderId"] = session.OrderId ?? "NA",
                ["capturedAmount"] = session.Captured,
                ["refundedAmount"] = session.Refunded,
                ["targetTransactionId"] = session.TransactionId ?? "NA",
                ["lastTransactionType"] = session.LastOperation,
                ["history"] = new JsonArray(),
            },
        });
    }

    private HttpResponseMessage Search(string reference)
    {
        // An unpaid session has no order yet, so it isn't found. The search is a partial match, as Kashier's is.
        var orders = new JsonArray();
        foreach (var session in _sessions.Values.Where(s => s.OrderId is not null && s.Reference.Contains(reference, StringComparison.Ordinal)))
        {
            orders.Add(new JsonObject
            {
                ["orderId"] = session.OrderId,
                ["merchantOrderId"] = session.Reference,
                ["sessionId"] = session.Id,
                ["status"] = session.Status == "PAID" ? "CAPTURED" : session.Status,
                ["totalCapturedAmount"] = session.Captured,
                ["totalRefundedAmount"] = session.Refunded,
            });
        }

        return Answer(HttpStatusCode.OK, new JsonObject { ["status"] = "SUCCESS", ["message"] = "Orders retrieved successfully", ["data"] = orders });
    }

    private HttpResponseMessage Operate(string orderId, JsonNode request)
    {
        var session = _sessions.Values.Single(s => s.OrderId == orderId);
        string operation = request["apiOperation"]!.GetValue<string>();
        decimal amount;
        if (operation == "REFUND")
        {
            decimal left = session.Captured - session.Refunded;
            amount = request["transaction"]?["amount"]?.GetValue<decimal>() ?? left;
            if (amount > left)
            {
                return Answer(HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Authorization error" });
            }

            session.Refunded += amount;
            session.Status = session.Refunded == session.Captured ? "REFUNDED" : "PARTIALLY_REFUNDED";
            session.LastOperation = "refund";
        }
        else
        {
            amount = session.Captured;
            session.Status = "VOIDED";
            session.LastOperation = "void";
        }

        return Answer(HttpStatusCode.OK, new JsonObject
        {
            ["status"] = "SUCCESS",
            ["response"] = new JsonObject
            {
                ["status"] = "SUCCESS",
                ["operation"] = session.LastOperation,
                ["amount"] = amount,
                ["currency"] = session.Currency,
                ["transactionId"] = $"TX-00000000{Next()}",
                ["orderId"] = orderId,
                ["merchantOrderId"] = session.Reference,
                ["totalCapturedAmount"] = session.Captured,
                ["totalRefundedAmount"] = session.Refunded,
            },
        });
    }

    private int Next() => ++_next;

    private sealed class Session
    {
        public required string Id { get; init; }

        public required string Reference { get; init; }

        public required decimal Amount { get; init; }

        public required string Currency { get; init; }

        public string Status { get; set; } = "CREATED";

        public string? OrderId { get; set; }

        public string? TransactionId { get; set; }

        public string LastOperation { get; set; } = "NA";

        public decimal Captured { get; set; }

        public decimal Refunded { get; set; }
    }
}
