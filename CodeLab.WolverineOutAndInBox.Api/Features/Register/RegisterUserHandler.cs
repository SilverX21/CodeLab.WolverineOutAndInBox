using CodeLab.WolverineOutAndInBox.Api.Data;
using CodeLab.WolverineOutAndInBox.Api.Entities;

using Wolverine;

namespace CodeLab.WolverineOutAndInBox.Api.Features.Register;

public class RegisterUserHandler(AppDbContext dbContext, IMessageBus bus, ILogger<RegisterUserHandler> logger)
{
    public async Task<Guid> Handle(RegisterUserCommand command, CancellationToken cancellationToken)
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
        await dbContext.SaveChangesAsync(cancellationToken);
        
        logger.LogInformation("User {FirstName} {LastName} registered successfully",  command.FirstName, command.LastName);
        
        //here we publish this to the message broker
        await bus.PublishAsync(new UserRegistered(user.Id, user.FirstName, user.LastName, user.Email));
        
        logger.LogInformation("User {FirstName} {LastName} was send to the message broker", command.FirstName, command.LastName);
        
        return user.Id;
    }
}