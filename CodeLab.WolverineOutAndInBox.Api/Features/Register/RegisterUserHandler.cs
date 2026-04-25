using CodeLab.WolverineOutAndInBox.Api.Data;
using CodeLab.WolverineOutAndInBox.Api.Entities;

using Wolverine;

namespace CodeLab.WolverineOutAndInBox.Api.Features.Register;

// Wolverine discovers this handler by convention: a class named *Handler with a Handle method.
// The DbContext, IMessageBus, and ILogger are injected automatically from the DI container.
public class RegisterUserHandler(AppDbContext dbContext, IMessageBus bus, ILogger<RegisterUserHandler> logger)
{
    // Wolverine wraps this method in an EF Core transaction automatically
    // (via UseEntityFrameworkCoreTransactions + AutoApplyTransactions in Program.cs).
    // The return value (Guid) is passed back to the caller of bus.InvokeAsync<Guid>.
    public async Task<Guid> Handle(RegisterUser command, CancellationToken cancellationToken)
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

        // Note: calling SaveChangesAsync here commits the user row before the outbox message
        // is written. This is safe because Wolverine's transaction still wraps the outbox write,
        // but the preferred pattern is to remove this call and let AutoApplyTransactions
        // commit everything — user row + outbox message — in a single atomic operation.
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {FirstName} {LastName} registered successfully", command.FirstName, command.LastName);

        // PublishAsync does NOT send directly to RabbitMQ here.
        // Because the durable outbox is enabled (UseDurableOutboxOnAllSendingEndpoints),
        // Wolverine writes UserRegistered to the outbox table in the same transaction.
        // A background relay process then forwards it to RabbitMQ after the transaction commits.
        // This guarantees the message is never lost even if the broker is temporarily down.
        await bus.PublishAsync(new UserRegistered(user.Id, user.FirstName, user.LastName, user.Email));

        logger.LogInformation("UserRegistered event queued in outbox for {FirstName} {LastName}", command.FirstName, command.LastName);

        return user.Id;
    }
}
