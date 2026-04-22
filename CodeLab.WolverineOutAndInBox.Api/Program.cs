using CodeLab.WolverineOutAndInBox.Api.Data;
using CodeLab.WolverineOutAndInBox.Api.Features.Onboarding;
using CodeLab.WolverineOutAndInBox.Api.Features.Register;

using Microsoft.EntityFrameworkCore;

using Scalar.AspNetCore;

using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("app-db");

builder.Host.UseWolverine(options =>
{
    options.UseRabbitMqUsingNamedConnection("rmq")
        .AutoProvision()
        .UseConventionalRouting();
    
    options.Policies.DisableConventionalLocalRouting();

    options.PersistMessagesWithPostgresql(connectionString!);
    
    options.UseEntityFrameworkCoreTransactions();
    options.Policies.AutoApplyTransactions();
    
    options.Policies.UseDurableOutboxOnAllSendingEndpoints();
    options.Policies.UseDurableInboxOnAllListeners();

    if (builder.Environment.IsDevelopment())
    {
        options.Durability.Mode = DurabilityMode.Solo;
    }
});

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Wolverine"))
    .WithMetrics(tracing => tracing.AddMeter("Wolverine"));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapPost("users", async (RegisterUserCommand command, IMessageBus bus, CancellationToken cancellationToken) =>
{
    var userId = await bus.InvokeAsync<Guid>(command, cancellationToken);
    
    return Results.Ok(new {userId});
});

app.MapGet("users/{id:guid}/verify-email", async (Guid id, IMessageBus bus, CancellationToken cancellationToken) =>
{
    await bus.PublishAsync(new VerifyUserEmail(id));
    return Results.Accepted();
});

app.Run();