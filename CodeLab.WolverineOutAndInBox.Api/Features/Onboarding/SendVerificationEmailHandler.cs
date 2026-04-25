namespace CodeLab.WolverineOutAndInBox.Api.Features.Onboarding;

public static class SendVerificationEmailHandler
{
    public static async Task<VerificationEmailSent> Handle(SendVerificationEmail command, ILogger logger)
    {
        logger.LogInformation("Sending verification email to {Email} for user {UserID}", command.Email, command.UserId);

        // Simulate email sending delay
        await Task.Delay(1_000);

        logger.LogInformation("Verification email sent to {Email} for user {UserID}", command.Email, command.UserId);

        return new VerificationEmailSent(command.UserId);
    }

}