using Wolverine;
using JasperFx.Core;

namespace CodeLab.WolverineOutAndInBox.Api.Features.Onboarding;

// Commands — an instruction to do something. Routed to a single handler.
// Named in imperative form (verb + noun). The handler is expected to act and may return a result.
public record SendVerificationEmail(Guid UserId, string Email);
public record SendWelcomeEmail(Guid UserId, string Email, string FirstName);

// Events — a notification that something already happened. Named in past tense.
// Published to all interested subscribers (handlers, sagas). No return value expected.
// These are produced by the email handlers and consumed by the saga to advance its state.
public record VerificationEmailSent(Guid Id);
public record WelcomeEmailSent(Guid Id);

// External trigger — published by the HTTP endpoint when the user clicks the verification link.
// Routed to UserOnboardingSaga.Handle(VerifyUserEmail) by matching the saga's Id.
public record VerifyUserEmail(Guid Id);

// Timeout — extends TimeoutMessage so Wolverine knows to schedule it for future delivery.
// When returned from the saga's Start method, Wolverine schedules it for 5 minutes from now.
// If the saga calls MarkCompleted() before the timeout fires, Wolverine cancels it automatically.
// If the user never verifies their email, this fires and closes the saga gracefully.
public record OnboardingTimedOut(Guid Id) : TimeoutMessage(5.Minutes());
