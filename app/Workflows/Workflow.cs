using System.Collections.Generic;

namespace app.Workflows;

/// <summary>
/// A declarative workflow: a named state machine with transitions and per-step
/// side effects. Built once at startup by <see cref="WorkflowBuilder"/>,
/// consumed at runtime by <see cref="WorkflowEngine"/>, and rendered on
/// <c>WorkflowViewPage</c> as a readable spec.
/// </summary>
public sealed class Workflow
{
    public required string Name { get; init; }
    public required string InitialState { get; init; }
    public required IReadOnlyDictionary<string, State> States { get; init; }
}

public sealed record State(string Name, IReadOnlyList<Transition> Transitions);

/// <summary>
/// One branch off a state. The first transition whose <see cref="Guard"/> returns
/// true is fired. <see cref="GuardDescription"/> is the prose that shows up on
/// WorkflowViewPage so non-expert readers can reason about the machine.
/// </summary>
public sealed record Transition(
    string Trigger,
    string? GuardDescription,
    Func<WorkflowContext, bool> Guard,
    IReadOnlyList<WorkflowStep> Steps,
    string Next);

/// <summary>
/// A single side effect inside a transition. <see cref="StepId"/> is the
/// machine-readable value stored in <c>workflow_events.step_id</c>;
/// <see cref="Description"/> is the human-readable one the view page renders.
/// </summary>
public sealed record WorkflowStep(
    string Description,
    string StepId,
    Func<WorkflowContext, Task> Run);
