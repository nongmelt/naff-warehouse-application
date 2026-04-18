using app.Workflows;
using app.Workflows.Definitions;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

/// <summary>
/// Renders a <see cref="Workflow"/> as a top-to-bottom list of state cards, each
/// listing its transitions as "on &lt;trigger&gt; when &lt;guard&gt; → &lt;next&gt;"
/// with indented step descriptions. A non-expert reads it like prose.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class WorkflowViewPage : ContentPage
{
    public WorkflowViewPage()
    {
        InitializeComponent();
        Render(PackingWorkflow.Build());
    }

    private void OnBack(object sender, EventArgs e)
        => Shell.Current.GoToAsync("//home");

    private void OnShowPacking(object sender, EventArgs e)
        => Render(PackingWorkflow.Build());

    private void OnShowQc(object sender, EventArgs e)
        => Render(QcWorkflow.Build());

    private void Render(Workflow wf)
    {
        HeaderLabel.Text = $"{wf.Name} Workflow";
        StatesLayout.Children.Clear();

        // Traverse in definition order — the builder preserves insertion order via OrderedDictionary.
        foreach (var kv in wf.States)
        {
            var isInitial = kv.Key == wf.InitialState;
            StatesLayout.Children.Add(BuildStateCard(kv.Value, isInitial));
        }
    }

    private static Border BuildStateCard(State state, bool isInitial)
    {
        var header = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
        header.Children.Add(new Label
        {
            Text = state.Name,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#111827"),
            VerticalOptions = LayoutOptions.Center,
        });
        if (isInitial)
        {
            header.Children.Add(new Border
            {
                BackgroundColor = Color.FromArgb("#dbeafe"),
                Stroke          = Color.FromArgb("#93c5fd"),
                StrokeThickness = 1,
                StrokeShape     = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding         = new Thickness(6, 2),
                Content         = new Label
                {
                    Text      = "initial",
                    FontSize  = 10,
                    TextColor = Color.FromArgb("#1d4ed8"),
                },
            });
        }

        var body = new VerticalStackLayout { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };

        if (state.Transitions.Count == 0)
        {
            body.Children.Add(new Label
            {
                Text = "(terminal — no outgoing transitions)",
                FontSize = 11,
                TextColor = Color.FromArgb("#9ca3af"),
                FontAttributes = FontAttributes.Italic,
            });
        }

        foreach (var t in state.Transitions)
            body.Children.Add(BuildTransition(t));

        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Children.Add(header);
        stack.Children.Add(body);

        return new Border
        {
            BackgroundColor = Colors.White,
            Stroke          = Color.FromArgb("#e5e7eb"),
            StrokeThickness = 1,
            StrokeShape     = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Padding         = new Thickness(16, 14),
            Content         = stack,
        };
    }

    private static VerticalStackLayout BuildTransition(Transition t)
    {
        var line = new FormattedString();
        line.Spans.Add(new Span { Text = "on ", FontSize = 12, TextColor = Color.FromArgb("#6b7280") });
        line.Spans.Add(new Span
        {
            Text = t.Trigger,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#0f766e"),
        });
        if (!string.IsNullOrWhiteSpace(t.GuardDescription))
        {
            line.Spans.Add(new Span { Text = "  when ", FontSize = 12, TextColor = Color.FromArgb("#6b7280") });
            line.Spans.Add(new Span
            {
                Text = t.GuardDescription,
                FontSize = 12,
                FontAttributes = FontAttributes.Italic,
                TextColor = Color.FromArgb("#374151"),
            });
        }
        line.Spans.Add(new Span { Text = "  →  ", FontSize = 12, TextColor = Color.FromArgb("#6b7280") });
        line.Spans.Add(new Span
        {
            Text = t.Next,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#2563eb"),
        });

        var result = new VerticalStackLayout { Spacing = 3 };
        result.Children.Add(new Label { FormattedText = line });

        foreach (var step in t.Steps)
        {
            result.Children.Add(new Label
            {
                Text     = $"      • {step.Description}   [{step.StepId}]",
                FontSize = 11,
                TextColor = Color.FromArgb("#6b7280"),
            });
        }

        return result;
    }
}
