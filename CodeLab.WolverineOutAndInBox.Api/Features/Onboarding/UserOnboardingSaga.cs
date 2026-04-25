using CodeLab.WolverineOutAndInBox.Api.Features.Register;
using Wolverine;

namespace CodeLab.WolverineOutAndInBox.Api.Features.Onboarding;

// A Wolverine Saga is a long-running, stateful process that coordinates multiple steps
// across time and message exchanges. Wolverine persists the saga's public properties
// to the PostgreSQL saga store between messages, so it survives process restarts.
//
// Wolverine correlates incoming messages to this saga instance by matching the message's Id
// property to the saga's Id property. The convention is: if the message has a property
// named {SagaTypeName}Id or Id, Wolverine uses it as the correlation key.
//
// Onboarding flow:
//   UserRegistered → Start → [SendVerificationEmail, OnboardingTimedOut scheduled]
//   VerificationEmailSent → Handle → state updated
//   VerifyUserEmail (HTTP) → Handle → SendWelcomeEmail dispatched
//   WelcomeEmailSent → Handle → MarkCompleted
//   OnboardingTimedOut → Handle → MarkCompleted (if user never verified)
public class UserOnboardingSaga : Saga
{
    public Guid Id { get; set; }
    public string Email { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }

    // State flags guard against out-of-order message delivery.
    // Never assume messages arrive in sequence — always check current state.
    public bool IsVerificationEmailSent { get; set; }
    public bool IsEmailVerified { get; set; }
    public bool IsWelcomeEmailSent { get; set; }

    public DateTime StartedAt { get; set; }

    // Static Start method — Wolverine calls this when a UserRegistered event arrives
    // and no existing saga instance is found. The method must be static and return
    // a tuple whose first element is the saga instance to persist.
    //
    // The remaining tuple elements are messages Wolverine dispatches atomically
    // after persisting the saga — meaning the saga state and the outgoing messages
    // are written to the DB in the same transaction.
    //
    // OnboardingTimedOut is a TimeoutMessage: Wolverine schedules it for 5 minutes
    // from now automatically. It is cancelled if MarkCompleted() is called first.
    public static (UserOnboardingSaga, SendVerificationEmail, OnboardingTimedOut) Start(
        UserRegistered @event, ILogger<UserOnboardingSaga> logger)
    {
        logger.LogInformation("Starting onboarding saga for user {UserID}", @event.Id);

        var saga = new UserOnboardingSaga
        {
            Id = Guid.NewGuid(),
            Email = @event.Email,
            FirstName = @event.FirstName,
            LastName = @event.LastName,
            StartedAt = DateTime.UtcNow
        };

        return (saga,
            new SendVerificationEmail(saga.Id, saga.Email),
            new OnboardingTimedOut(saga.Id));
    }

    // Confirmation that SendVerificationEmailHandler completed successfully.
    // Updates the state flag so we know the verification step was dispatched.
    // No message returned — this step doesn't trigger any further action.
    public void Handle(VerificationEmailSent @event, ILogger<UserOnboardingSaga> logger)
    {
        logger.LogInformation("Verification email sent for user {UserID}", Id);
        IsVerificationEmailSent = true;
    }

    // Triggered when the user clicks the verification link (via GET /users/{id}/verify-email).
    // Returning a message from a Handle method tells Wolverine to dispatch it automatically —
    // no need to call bus.PublishAsync manually.
    public SendWelcomeEmail Handle(VerifyUserEmail command, ILogger<UserOnboardingSaga> logger)
    {
        logger.LogInformation("Email verified for user {UserID}", Id);
        IsEmailVerified = true;
        return new SendWelcomeEmail(Id, Email, FirstName);
    }

    // Confirmation that SendWelcomeEmailHandler completed successfully.
    // MarkCompleted() removes the saga from the PostgreSQL store and cancels
    // any pending scheduled messages (e.g. OnboardingTimedOut) automatically.
    public void Handle(WelcomeEmailSent @event, ILogger<UserOnboardingSaga> logger)
    {
        logger.LogInformation("Onboarding complete for user {UserID}", Id);
        IsWelcomeEmailSent = true;
        MarkCompleted();
    }

    // Fires if the user never clicks the verification link within 5 minutes.
    // The IsEmailVerified guard handles the rare race condition where VerifyUserEmail
    // and OnboardingTimedOut are processed concurrently — the timeout should be a no-op
    // if verification already succeeded.
    // Extend this handler to send a reminder email or flag the account for follow-up.
    public void Handle(OnboardingTimedOut timeout, ILogger<UserOnboardingSaga> logger)
    {
        if (IsEmailVerified)
        {
            logger.LogWarning("Timeout ignored — email already verified for user {UserID}", Id);
            MarkCompleted();
            return;
        }

        logger.LogInformation("Onboarding timed out for user {UserID} — email not verified", Id);
        MarkCompleted();
    }

    // NotFound handlers are called when a message arrives for a saga that no longer exists
    // (already completed or never started). Without these, Wolverine throws SagaNotFoundException,
    // which triggers retries and eventually lands in the dead-letter queue.
    // Always implement NotFound for every message type a saga handles.
    public static void NotFound(VerifyUserEmail command, ILogger<UserOnboardingSaga> logger)
    {
        logger.LogWarning("VerifyUserEmail received but onboarding saga {UserID} no longer exists — possibly already completed", command.Id);
    }

    public static void NotFound(OnboardingTimedOut timeout, ILogger<UserOnboardingSaga> logger)
    {
        logger.LogWarning("OnboardingTimedOut received for already-completed saga {UserID}", timeout.Id);
    }
}
