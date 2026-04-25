using CodeLab.WolverineOutAndInBox.Api.Data;
using CodeLab.WolverineOutAndInBox.Api.Entities;
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
    // Connect to RabbitMQ using the named connection from Aspire ("rmq").
    // AutoProvision: creates exchanges and queues if they don't exist.
    // UseConventionalRouting: derives exchange/queue names from message type names automatically.
    options.UseRabbitMqUsingNamedConnection("rmq")
        .AutoProvision()
        .UseConventionalRouting();

    // Disable Wolverine's default local in-memory routing so all messages go through RabbitMQ.
    // Without this, Wolverine would route messages to local queues when a local handler exists,
    // bypassing the broker and the durable outbox.
    options.Policies.DisableConventionalLocalRouting();

    // Store Wolverine's envelope table (outbox/inbox) in PostgreSQL.
    // This is what makes messaging durable — messages survive process restarts.
    options.PersistMessagesWithPostgresql(connectionString!);

    // Integrate Wolverine with EF Core so they share the same DbContext transaction.
    // UseEntityFrameworkCoreTransactions: makes Wolverine aware of the EF Core DbContext.
    // AutoApplyTransactions: automatically wraps every handler in a transaction —
    // you don't need to manage transactions manually in handlers.
    options.UseEntityFrameworkCoreTransactions();
    options.Policies.AutoApplyTransactions();

    // Durable outbox: messages published inside a handler are written to the DB first,
    // then relayed to RabbitMQ by a background process. Guarantees no message is lost
    // even if the broker is temporarily unavailable or the process crashes mid-handler.
    options.Policies.UseDurableOutboxOnAllSendingEndpoints();

    // Durable inbox: before processing a message, Wolverine records its ID in the DB.
    // If the same message arrives again (RabbitMQ at-least-once delivery), it's deduplicated.
    options.Policies.UseDurableInboxOnAllListeners();

    // Solo mode disables leader election and distributed locking — safe only when a single
    // instance is running. NEVER use in staging or production with multiple instances,
    // as it will cause duplicate message processing and race conditions.
    if (builder.Environment.IsDevelopment())
    {
        options.Durability.Mode = DurabilityMode.Solo;
    }
});

// Add Wolverine's built-in OpenTelemetry sources so spans and metrics are included
// in the tracing/metrics pipeline configured by ServiceDefaults.
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

    var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();
}

app.UseHttpsRedirection();

// -------------------------------------------------------------------------
// Variant 1: Outbox via Wolverine handler (recommended approach)
//
// bus.InvokeAsync dispatches RegisterUser to RegisterUserHandler.
// Wolverine wraps the handler in an EF Core transaction automatically.
// Any bus.PublishAsync calls inside the handler write to the outbox table
// in the same transaction — not directly to RabbitMQ. A background relay
// then forwards them to the broker after the transaction commits.
// -------------------------------------------------------------------------
app.MapPost("users", async (RegisterUser command, IMessageBus bus, CancellationToken cancellationToken) =>
{
    var userId = await bus.InvokeAsync<Guid>(command, cancellationToken);

    return Results.Ok(new {userId});
});

// -------------------------------------------------------------------------
// Variant 2: Outbox via IDbContextOutbox<T> (manual, outside a handler)
//
// Use this when you need outbox guarantees from a minimal API endpoint
// without routing through a Wolverine handler. IDbContextOutbox<AppDbContext>
// ties the outbox to the same DbContext instance that EF Core uses, so
// the DB write and the outbox message commit atomically via
// SaveChangesAndFlushMessagesAsync.
// -------------------------------------------------------------------------
app.MapPost("users/outside-wolverine-1", async (
    RegisterUser command,
    IDbContextOutbox<AppDbContext> outbox,
    CancellationToken cancellationToken) =>
{
    var user = new User()
    {
        Id = Guid.NewGuid(),
        FirstName = command.FirstName,
        LastName = command.LastName,
        Email = command.Email,
        CreatedAt = DateTime.UtcNow
    };

    // Use outbox.DbContext to ensure the entity write and the outbox message
    // are on the same DbContext instance — required for atomic commit.
    await outbox.DbContext.Users.AddAsync(user, cancellationToken);

    // Enqueue the message into the outbox (not sent to RabbitMQ yet).
    await outbox.PublishAsync(new UserRegistered(user.Id, user.FirstName, user.LastName, user.Email));

    // Commit the DB write and the outbox message in a single transaction.
    // The background relay will forward the message to RabbitMQ afterwards.
    await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

    return Results.Ok(new {userId = user.Id});
});

// -------------------------------------------------------------------------
// Variant 3: Outbox via IDbContextOutbox (non-generic) with explicit Enroll
//
// Use this when you already have a DbContext instance injected separately
// and want to enroll it into the outbox manually. Functionally identical
// to Variant 2, but shows how to connect an existing DbContext to the outbox
// after the fact via outbox.Enroll(dbContext).
// -------------------------------------------------------------------------
app.MapPost("users/outside-wolverine-2", async (
    RegisterUser command,
    AppDbContext dbContext,
    IDbContextOutbox outbox,
    CancellationToken cancellationToken) =>
{
    var user = new User()
    {
        Id = Guid.NewGuid(),
        FirstName = command.FirstName,
        LastName = command.LastName,
        Email = command.Email,
        CreatedAt = DateTime.UtcNow
    };

    await dbContext.Users.AddAsync(user, cancellationToken);

    // Enroll the existing DbContext into the outbox so they share the same transaction.
    outbox.Enroll(dbContext);

    await outbox.PublishAsync(new UserRegistered(user.Id, user.FirstName, user.LastName, user.Email));
    await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

    return Results.Ok(new {userId = user.Id});
});

// -------------------------------------------------------------------------
// External trigger for the onboarding saga.
//
// When the user clicks the verification link, this endpoint fires VerifyUserEmail
// into the message bus. Wolverine routes it to UserOnboardingSaga.Handle(VerifyUserEmail),
// which updates saga state and dispatches SendWelcomeEmail.
// The saga is loaded from the PostgreSQL saga store by matching the message's Id.
// -------------------------------------------------------------------------
app.MapGet("users/{id:guid}/verify-email", async (Guid id, IMessageBus bus, CancellationToken cancellationToken) =>
{
    await bus.PublishAsync(new VerifyUserEmail(id));
    return Results.Accepted();
});

app.Run();
