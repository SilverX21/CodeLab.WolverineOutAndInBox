namespace CodeLab.WolverineOutAndInBox.Api.Features.Onboarding;

public class Message
{
    
}

public record SendVerificationEmail(Guid Id, string Email);

public record OboardingTimedOut(Guid Id);

public record VerifyUserEmail(Guid Id);