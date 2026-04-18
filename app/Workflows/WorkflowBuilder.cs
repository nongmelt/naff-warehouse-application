using System.Collections.Generic;

namespace app.Workflows;

/// <summary>
/// Fluent builder: <c>new WorkflowBuilder("Packing").Initial("idle").State("idle", s =&gt; s.On(...).Do(...).GoTo(...)).Build()</c>.
/// </summary>
public sealed class WorkflowBuilder
{
    private readonly string                     _name;
    private          string?                    _initial;
    private readonly Dictionary<string, State>  _states = new();

    public WorkflowBuilder(string name) => _name = name;

    public WorkflowBuilder Initial(string state)
    {
        _initial = state;
        return this;
    }

    public WorkflowBuilder State(string name, Action<StateBuilder> configure)
    {
        var sb = new StateBuilder(name);
        configure(sb);
        _states[name] = sb.Build();
        return this;
    }

    public Workflow Build()
    {
        if (_initial is null)
            throw new InvalidOperationException($"Workflow '{_name}': Initial(...) was not called.");
        if (!_states.ContainsKey(_initial))
            throw new InvalidOperationException(
                $"Workflow '{_name}': initial state '{_initial}' was never registered via State(...).");

        return new Workflow
        {
            Name         = _name,
            InitialState = _initial,
            States       = _states,
        };
    }
}

public sealed class StateBuilder
{
    private readonly string                _name;
    private readonly List<TransitionDraft> _drafts = new();

    internal StateBuilder(string name) => _name = name;

    public TransitionBuilder On(string trigger)
    {
        var draft = new TransitionDraft(trigger);
        _drafts.Add(draft);
        return new TransitionBuilder(this, draft);
    }

    internal State Build()
    {
        var transitions = _drafts.Select(d => d.ToTransition()).ToList();
        return new State(_name, transitions);
    }
}

/// <summary>
/// A transition-in-progress inside the fluent builder. Guard defaults to "always",
/// steps accumulate as <see cref="Do"/> calls fire, and <see cref="GoTo"/> seals
/// the transition and hands control back to the <see cref="StateBuilder"/>.
/// </summary>
public sealed class TransitionBuilder
{
    private readonly StateBuilder     _state;
    private readonly TransitionDraft  _draft;

    internal TransitionBuilder(StateBuilder state, TransitionDraft draft)
    {
        _state = state;
        _draft = draft;
    }

    public TransitionBuilder When(string description, Func<WorkflowContext, bool> guard)
    {
        _draft.GuardDescription = description;
        _draft.Guard            = guard;
        return this;
    }

    public TransitionBuilder Do(string stepId, string description, Func<WorkflowContext, Task> run)
    {
        _draft.Steps.Add(new WorkflowStep(description, stepId, run));
        return this;
    }

    public StateBuilder GoTo(string next)
    {
        _draft.Next = next;
        return _state;
    }

    public TransitionBuilder On(string trigger) => _state.On(trigger);
}

internal sealed class TransitionDraft
{
    public string                        Trigger           { get; }
    public string?                       GuardDescription  { get; set; }
    public Func<WorkflowContext, bool>   Guard             { get; set; } = _ => true;
    public List<WorkflowStep>            Steps             { get; }      = new();
    public string?                       Next              { get; set; }

    public TransitionDraft(string trigger) => Trigger = trigger;

    public Transition ToTransition() =>
        new(Trigger, GuardDescription, Guard, Steps,
            Next ?? throw new InvalidOperationException(
                $"Transition on '{Trigger}' is missing a GoTo(...) clause."));
}
