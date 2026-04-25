namespace CodeLab.WolverineOutAndInBox.Api.Features.Onboarding;

public static class SendWelcomeEmailHandler
{
    public static async Task<WelcomeEmailSent> Handle(SendWelcomeEmail command, ILogger logger)
    {
        logger.LogInformation("Sending welcome email to {FirstName} at {Email}", command.FirstName, command.Email);

        // Simulate email sending delay
        await Task.Delay(1000);

        logger.LogInformation("Welcome email sent to {Email}", command.Email);

        return new WelcomeEmailSent(command.UserId);
    }

}