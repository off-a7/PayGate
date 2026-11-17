using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayGate;
using PayGate.AspNetCore;
using PayGate.Sample.WebApi.Data;
using PayGate.Sample.WebApi.Fulfillment;
using PayGate.Sample.WebApi.Payments;
using PayGate.Testing;

var builder = WebApplication.CreateBuilder(args);

// The app's settings. Payments:Account picks the provider; nothing else in the app depends on it.
builder.Services.AddOptions<SamplePaymentOptions>()
    .BindConfiguration(SamplePaymentOptions.SectionName)
    .Validate(o => o.PublicBaseUrl.IsAbsoluteUri && o.PublicBaseUrl.AbsolutePath.EndsWith('/'), "Payments:PublicBaseUrl must be absolute and end with '/'.")
    .Validate(o => PaymentAccount.TryParse(o.Account, out _), "Payments:Account must look like 'fake' or 'kashier:secondary'.")
    .ValidateOnStart();
var settings = builder.Configuration.GetSection(SamplePaymentOptions.SectionName).Get<SamplePaymentOptions>() ?? new SamplePaymentOptions();
var account = PaymentAccount.Parse(settings.Account);

// App-owned state: orders, attempts and the webhook inbox. PayGate stores nothing.
builder.Services.AddDbContext<SampleDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Sample")));
builder.Services.AddScoped<PaymentUpdater>();
builder.Services.AddScoped<PaymentReconciler>();
builder.Services.AddSingleton<IOrderFulfillment, LoggingOrderFulfillment>();
builder.Services.AddHostedService<ReconciliationService>();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// PayGate: the provider, the webhook endpoint, and the app's handler for payment events.
builder.Services.AddPayGate()
    .AddConfiguredProvider(account, builder.Configuration)
    .AddEventHandler<PaymentWebhookHandler>()
    .AddWebhookEndpoints(options =>
    {
        foreach (var url in settings.AllowedReturnUrls)
        {
            options.AllowedReturnUrls.Add(url);
        }
    })
    .ConfigureWebhooks(account, options => options.ConfirmWithProvider = settings.ConfirmWithProvider);

var app = builder.Build();

// A real app runs migrations. The sample creates its tables on first start to stay small.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();

// Providers do not log in: the signature is what authenticates a webhook.
app.MapPayGateWebhooks(settings.WebhookPrefix).AllowAnonymous();
app.MapOrderEndpoints();
if (app.Environment.IsDevelopment() && account.Provider == FakePaymentGateway.ProviderId)
{
    app.MapFakeCustomerEndpoints(account);
}

app.MapGet("/", () => "PayGate sample. See samples/README.md for the walkthrough.");

await app.RunAsync();
