# Wolverine Patterns — Documentation

This project demonstrates reliable, message-driven architecture using **Wolverine**, **EF Core**, **RabbitMQ**, and **PostgreSQL**, orchestrated via **.NET Aspire**.

The three core patterns covered here — **Outbox**, **Inbox**, and **Saga** — work together to guarantee that messages are never lost, never processed twice, and that long-running workflows survive restarts, failures, and timeouts.

---

## Table of Contents

1. [Outbox Pattern](#outbox-pattern)
2. [Inbox Pattern](#inbox-pattern)
3. [Saga Pattern](#saga-pattern)
4. [Best Practices & Pitfalls](#best-practices--pitfalls)

---

## Outbox Pattern

### Concept

The outbox pattern solves the **dual-write problem**: when you need to both save data to a database *and* publish a message to a broker, you can't do both atomically. If the DB write succeeds but the broker publish fails (or the process crashes in between), you've lost the message.

The solution: **write the message to the database in the same transaction as your business data**. A background relay process then reads unsent messages from the DB and forwards them to the broker. This way, either both the business data and the message are committed, or neither is.

```
┌─────────────────────────────────────┐
│           Single Transaction         │
│                                     │
│  ┌─────────────┐  ┌───────────────┐ │
│  │  users table│  │ outbox table  │ │
│  │  (new user) │  │ (new message) │ │
│  └─────────────┘  └───────────────┘ │
└─────────────────────────────────────┘
             │
             ▼
    Background relay process
             │
             ▼
        RabbitMQ broker
```

### Implementation in this project

This project shows **two ways** to use the outbox with Wolverine.

---

#### Variant 1: Via a Wolverine Handler (recommended)

**Endpoint:** `POST /users`

```csharp
app.MapPost("users", async (RegisterUser command, IMessageBus bus, CancellationToken cancellationToken) =>
{
    var userId = await bus.InvokeAsync<Guid>(command, cancellationToken);
    return Results.Ok(new { userId });
});
```

**Handler:** `RegisterUserHandler.cs`

```csharp
public async Task<Guid> Handle(RegisterUser command, CancellationToken cancellationToken)
{
    var user = new User { ... };
    await dbContext.Users.AddAsync(user, cancellationToken);
    await dbContext.SaveChangesAsync(cancellationToken); // ⚠️ see pitfalls section
    await bus.PublishAsync(new UserRegistered(...));
    return user.Id;
}
```

Wolverine wraps the entire handler in a transaction (via `options.UseEntityFrameworkCoreTransactions()` + `options.Policies.AutoApplyTransactions()`). Any message published via `bus.PublishAsync` inside the handler is written to the outbox table in the same transaction — not sent directly to RabbitMQ. After the transaction commits, Wolverine's relay forwards them.

**Why this is the preferred approach:** You write normal business code. Wolverine handles atomicity, retry, and relay transparently.

---

#### Variant 2: Via `IDbContextOutbox` (manual, outside a handler)

**Endpoint:** `POST /users/outside-wolverine-1`

```csharp
app.MapPost("users/outside-wolverine-1", async (
    RegisterUser command,
    AppDbContext dbContext,
    IDbContextOutbox<AppDbContext> outbox,
    CancellationToken cancellationToken) =>
{
    var user = new User { ... };
    await outbox.DbContext.Users.AddAsync(user, cancellationToken);
    await outbox.PublishAsync(new UserRegistered(...));
    await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    return Results.Ok(new { userId = user.Id });
});
```

Here you are **outside a Wolverine handler**, so auto-transactions don't apply. `IDbContextOutbox<AppDbContext>` gives you explicit control: you enqueue the message with `PublishAsync` and then commit everything atomically with `SaveChangesAndFlushMessagesAsync`.

**When to use this:** Minimal API endpoints or other code that lives outside Wolverine's handler pipeline but still needs outbox guarantees.

---

### Outbox: at a glance

| | Handler variant | `IDbContextOutbox` variant |
|---|---|---|
| Transaction managed by | Wolverine (automatic) | You (explicit) |
| Commit call | Not needed (auto) | `SaveChangesAndFlushMessagesAsync` |
| Best for | Handler pipeline | Minimal APIs, background services |

---

## Inbox Pattern

### Concept

Message brokers like RabbitMQ guarantee **at-least-once delivery** — meaning a message may be delivered more than once (due to retries, network issues, or redelivery after a crash). Without protection, your handler could process the same message twice, causing duplicate users, double emails, or corrupted state.

The **inbox** solves this by storing the ID of every processed message in the database. Before processing, Wolverine checks: "have I seen this message before?" If yes, it skips processing. If no, it processes and records the ID — all within the same transaction.

```
Message arrives from RabbitMQ
         │
         ▼
Is message ID in inbox table?
    ├── YES → discard (already processed)
    └── NO  → process + record ID (same transaction)
```

### Implementation in this project

Configured in `Program.cs` with a single line:

```csharp
options.Policies.UseDurableInboxOnAllListeners();
```

This applies inbox deduplication to **every** RabbitMQ consumer in the application automatically. No per-handler configuration needed.

The inbox table is created in your PostgreSQL database via `modelBuilder.MapWolverineEnvelopeStorage()` in `AppDbContext.OnModelCreating`.

### The full reliable messaging flow

Outbox and inbox are two halves of the same guarantee:

```
Producer side                        Consumer side
─────────────────────────────────────────────────────
Business data + message              Message arrives
written atomically to DB             │
         │                           ▼
         ▼                    Inbox deduplication check
Background relay forwards            │
message to RabbitMQ                  ▼
                              Process + record (atomic)
```

- **Outbox** guarantees the message *leaves* your system exactly once
- **Inbox** guarantees the message *is processed* exactly once
- Together: **effectively-once** end-to-end delivery

---

## Saga Pattern

### Concept

A **saga** is a long-running process that coordinates multiple steps across time — steps that may involve different services, scheduled timeouts, or external triggers (like a user clicking a link in an email).

Instead of chaining handlers directly (handler A calls handler B calls handler C), a saga:

- **Persists its state** between steps (survives restarts)
- **Reacts to events** from multiple sources
- **Manages timeouts** automatically
- **Completes gracefully** when done, or when things go wrong

Sagas are the right tool when a workflow spans more than one message exchange or involves waiting for external input.

### `UserOnboardingSaga` walkthrough

The saga is triggered when a user registers and guides them through email verification and welcome email — with a 5-minute timeout if they never verify.

```
UserRegistered
      │
      ▼
 Saga starts ──────────────────────────────────────┐
      │                                            │
      ├──► SendVerificationEmail (dispatched)      │ OnboardingTimedOut
      └──► OnboardingTimedOut scheduled (5 min)    │ (fires if no verify)
                                                   │
VerificationEmailSent                              │
      │                                            │
      ▼                                            │
 IsVerificationEmailSent = true                    │
                                                   │
VerifyUserEmail (HTTP trigger)                     │
      │                                            │
      ▼                                            │
 IsEmailVerified = true                            │
 ──► SendWelcomeEmail (dispatched)                 │
                                                   │
WelcomeEmailSent                                   │
      │                                            │
      ▼                                            │
 IsWelcomeEmailSent = true                         │
 MarkCompleted() ◄─────────────────────────────────┘
```

---

#### Starting the saga

```csharp
public static (UserOnboardingSaga, SendVerificationEmail, OnboardingTimedOut) Start(
    UserRegistered @event, ILogger<UserOnboardingSaga> logger)
{
    var saga = new UserOnboardingSaga { Id = Guid.NewGuid(), ... };
    return (saga,
        new SendVerificationEmail(saga.Id, saga.Email),
        new OnboardingTimedOut(saga.Id));
}
```

The tuple return is Wolverine's convention for saga start methods: the first element is the saga to persist, the rest are messages to dispatch. Wolverine persists the saga state and dispatches `SendVerificationEmail` and `OnboardingTimedOut` **atomically in the same transaction**.

`OnboardingTimedOut` is a `TimeoutMessage` — Wolverine automatically schedules it for 5 minutes from now. If `MarkCompleted()` is called before then, Wolverine cancels it automatically.

---

#### Handling steps

Each `Handle` method receives a message, updates state, and optionally returns a new message to dispatch:

```csharp
// Confirmation that the verification email was dispatched
public void Handle(VerificationEmailSent @event, ...)
    => IsVerificationEmailSent = true;

// External trigger: user clicked the verification link
public SendWelcomeEmail Handle(VerifyUserEmail command, ...)
{
    IsEmailVerified = true;
    return new SendWelcomeEmail(Id, Email, FirstName); // dispatched automatically
}

// Welcome email sent — onboarding complete
public void Handle(WelcomeEmailSent @event, ...)
{
    IsWelcomeEmailSent = true;
    MarkCompleted(); // removes saga from the store
}
```

---

#### Timeout handling

```csharp
public void Handle(OnboardingTimedOut timeout, ...)
{
    if (IsEmailVerified) return; // guard: timeout may arrive after verify in rare cases
    MarkCompleted();
}
```

The `OnboardingTimedOut` message extends `TimeoutMessage(5.Minutes())`. If the user never clicks the verification link within 5 minutes, this fires and closes the saga. Extend this handler to send a reminder or flag the account for follow-up.

---

#### `NotFound` handlers

```csharp
public static void NotFound(VerifyUserEmail command, ...)
    => logger.LogWarning("Saga {Id} no longer exists", command.Id);

public static void NotFound(OnboardingTimedOut timeout, ...)
    => logger.LogWarning("Timeout for already-completed saga {Id}", timeout.Id);
```

These are called when a message arrives for a saga that no longer exists (already completed or never started). Without `NotFound` handlers, Wolverine throws an exception — which triggers retries, eventually landing in the dead-letter queue. Always implement them.

---

## Best Practices & Pitfalls

### 1. Don't call `SaveChangesAsync` manually in handlers

**Broad concept:** In transactional systems, calling commit at the wrong time breaks atomicity.

**Wolverine rule:** When `options.Policies.AutoApplyTransactions()` is configured, Wolverine wraps your handler in a transaction and commits it after the handler returns. If you call `SaveChangesAsync` inside the handler yourself, you're committing the DB write *before* the outbox message is written — breaking the atomicity guarantee. The message could be lost if the process crashes after your `SaveChangesAsync` but before Wolverine's commit.

**In this project:** `RegisterUserHandler` calls `SaveChangesAsync` explicitly. This is a known trade-off — it works because the auto-transaction still wraps the outbox write — but it's safer to remove it and let Wolverine commit everything at once.

> **Rule:** Remove `SaveChangesAsync` from handlers and let `AutoApplyTransactions` handle it.

---

### 2. Always call `MarkCompleted()` in sagas

**Broad concept:** Long-running processes must have a defined termination.

**Wolverine rule:** Wolverine persists saga state in the database for as long as the saga is active. If you never call `MarkCompleted()`, the saga row stays in the DB indefinitely — even after the workflow logically ends. Over time this accumulates orphaned rows and can cause `NotFound` handlers to never fire (because the saga still "exists").

**In this project:** `UserOnboardingSaga` calls `MarkCompleted()` in both the happy path (`WelcomeEmailSent`) and the timeout path (`OnboardingTimedOut`).

> **Rule:** Every code path through a saga must eventually call `MarkCompleted()`.

---

### 3. Make handlers idempotent

**Broad concept:** At-least-once delivery means your handler may be called more than once for the same message. The inbox deduplicates at the Wolverine level, but your *business logic* must also be safe to run twice if you ever bypass the inbox or operate across systems.

**Wolverine rule:** Even with `UseDurableInboxOnAllListeners`, there are edge cases (e.g., DB rollback after inbox record was written). Design handlers to be idempotent: use upserts, check-before-insert, or naturally idempotent operations.

**In this project:** `RegisterUserHandler` does a blind `AddAsync`. A safer approach:

```csharp
// Instead of blindly inserting:
var existing = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == command.Email);
if (existing is not null) return existing.Id;
```

> **Rule:** Assume any handler can run more than once. Design accordingly.

---

### 4. Don't assume message ordering

**Broad concept:** Distributed systems do not guarantee message order. `VerificationEmailSent` could theoretically arrive before the saga's own start transaction fully commits in edge cases, or `WelcomeEmailSent` could arrive before `VerificationEmailSent` under load.

**Wolverine rule:** Use state flags to guard against out-of-order processing. Don't write logic that assumes "step 2 always comes after step 1".

**In this project:** `UserOnboardingSaga` uses `IsVerificationEmailSent`, `IsEmailVerified`, and `IsWelcomeEmailSent` flags — each step checks/sets state independently. The timeout handler also guards against the case where `OnboardingTimedOut` fires just after `VerifyUserEmail` is processed.

> **Rule:** Use state flags in sagas. Never assume message order.

---

### 5. Understand poison messages and dead-letter queues

**Broad concept:** A poison message is one that consistently causes a handler to throw an exception. Without a dead-letter strategy, Wolverine retries it indefinitely, blocking the consumer.

**Wolverine rule:** Wolverine has built-in retry policies. After exhausting retries, it moves the message to a dead-letter queue in RabbitMQ (or marks it as failed in the envelope store). You should monitor the dead-letter queue and have a strategy for reprocessing or alerting.

**In this project:** The default retry policy applies. To customize:

```csharp
options.Policies.OnException<SomeTransientException>()
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
```

> **Rule:** Always configure a dead-letter queue in RabbitMQ and monitor it. Implement `NotFound` handlers on sagas to prevent saga-related poison messages.

---

### 6. `DurabilityMode.Solo` is for development only

**Broad concept:** Distributed systems require coordination when multiple instances compete for the same messages.

**Wolverine rule:** `DurabilityMode.Solo` disables Wolverine's leader election and distributed locking — it assumes only one node is running. In production with multiple instances, this causes duplicate processing and race conditions on the envelope store.

**In this project:** Correctly gated to `IsDevelopment()`:

```csharp
if (builder.Environment.IsDevelopment())
    options.Durability.Mode = DurabilityMode.Solo;
```

> **Rule:** Never set `DurabilityMode.Solo` in staging or production. Remove it or gate it strictly.

---

### 7. Avoid publishing messages inside tight loops

**Broad concept:** Publishing a message per iteration of a loop multiplies the number of DB writes (each outbox entry is a row) and broker round-trips.

**Wolverine rule:** If you need to process a collection, batch your messages or use a single aggregate event. Publishing 10,000 individual messages in a loop is a common cause of performance degradation.

```csharp
// ❌ Avoid
foreach (var user in users)
    await bus.PublishAsync(new SendWelcomeEmail(user.Id, user.Email, user.FirstName));

// ✅ Prefer — publish one batch message and handle the collection in the handler
await bus.PublishAsync(new SendBulkWelcomeEmails(users.Select(u => u.Id).ToList()));
```

> **Rule:** One message per logical unit of work, not per item in a collection.

---

### 8. Always implement `NotFound` on sagas

**Broad concept:** In distributed systems, messages can arrive after their intended recipient is gone — due to retries, delayed delivery, or race conditions at completion.

**Wolverine rule:** If a message arrives for a saga that no longer exists and there is no `NotFound` handler, Wolverine throws `SagaNotFoundException`. This triggers retries and eventually lands in the dead-letter queue — noisy, wasteful, and misleading in logs.

**In this project:** Both `VerifyUserEmail` and `OnboardingTimedOut` have `NotFound` handlers that log a warning and return gracefully.

> **Rule:** For every message type that a saga handles, implement a corresponding static `NotFound` handler.
