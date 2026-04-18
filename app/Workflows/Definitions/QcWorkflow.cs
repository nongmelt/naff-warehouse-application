using System.Runtime.Versioning;

namespace app.Workflows.Definitions;

/// <summary>
/// QC: Idle → order-loaded → picking → passed (terminal) or held (partial).
///
/// Triggers:
///   <list type="bullet">
///     <item><c>tracking_scan</c> — operator scans a tracking/order number in the search box.</item>
///     <item><c>sku_scan</c> — operator scans an item barcode.</item>
///     <item><c>qty_entered</c> — operator types a number in a pick-qty field.</item>
///     <item><c>card_tap</c> — operator taps a product card (non-scan deduction).</item>
///     <item><c>reset</c> — operator hits reset on a passed order.</item>
///     <item><c>leave_page</c> — operator navigates away; held orders get saved.</item>
///   </list>
///
/// The <c>sequence_in_session</c> counter on <see cref="WorkflowContext"/>
/// starts at 1 on <c>tracking_scanned</c> and increments on every mutation —
/// that's the field that answers "which items get scanned first".
/// </summary>
[SupportedOSPlatform("windows")]
public static class QcWorkflow
{
    public const string Name = "QC";

    public static Workflow Build() =>
        new WorkflowBuilder(Name)
            .Initial("idle")

            // ── idle ─────────────────────────────────────────────────────────
            .State("idle", s => s
                .On("tracking_scan")
                    .When("tracking scanned", _ => true)
                    .Do("tracking_scanned", "load matching orders into session",
                        StationActions.LoadOrdersForTracking)
                    .GoTo("order-loaded"))

            // ── order-loaded ─────────────────────────────────────────────────
            .State("order-loaded", s => s
                .On("sku_scan")
                    .When("SKU matches a row with qty = 1",
                          c => c.QtyRemaining == 1)
                    .Do("item_scanned_auto", "auto-deduct 1",
                        StationActions.ApplySkuDeduction)
                    .GoTo("picking")

                .On("sku_scan")
                    .When("SKU matches a row with qty > 1",
                          c => c.QtyRemaining is > 1)
                    .Do("item_scanned_await_qty", "enter picking — await qty input",
                        StationActions.Noop)
                    .GoTo("picking")

                .On("sku_scan")
                    .When("SKU doesn't match any open order",
                          c => c.QtyRemaining is null or 0)
                    .Do("item_scan_rejected", "reject scan, keep state",
                        StationActions.LogScanRejected)
                    .GoTo("order-loaded")

                .On("card_tap")
                    .When("operator tapped a product card",
                          c => !string.IsNullOrWhiteSpace(c.Sku))
                    .Do("card_clicked", "deduct qty from tapped card",
                        StationActions.ApplyCardTap)
                    .GoTo("picking")

                .On("qty_entered")
                    .When("operator typed a number",
                          c => c.QtyEntered is > 0)
                    .Do("manual_qty_entered", "apply manual qty",
                        StationActions.ApplyManualQty)
                    .GoTo("picking")

                .On("tracking_scan")
                    .When("tracking re-scanned — reload session",
                          _ => true)
                    .Do("session_ended", "capture previous tracking for audit trail",
                        StationActions.CapturePreviousTracking)
                    .Do("tracking_scanned", "load matching orders into session",
                        StationActions.LoadOrdersForTracking)
                    .GoTo("order-loaded")

                .On("leave_page")
                    .When("operator navigated away",
                          _ => true)
                    .Do("order_held", "persist QC Hold for incomplete orders",
                        StationActions.SaveQcHold)
                    .GoTo("idle"))

            // ── picking ──────────────────────────────────────────────────────
            .State("picking", s => s
                .On("sku_scan")
                    .When("SKU matches a row with qty = 1",
                          c => c.QtyRemaining == 1)
                    .Do("item_scanned_auto", "auto-deduct 1",
                        StationActions.ApplySkuDeduction)
                    .GoTo("picking")

                .On("sku_scan")
                    .When("SKU matches a row with qty > 1",
                          c => c.QtyRemaining is > 1)
                    .Do("item_scanned_await_qty", "enter picking — await qty input",
                        StationActions.Noop)
                    .GoTo("picking")

                .On("sku_scan")
                    .When("SKU doesn't match any open order",
                          c => c.QtyRemaining is null or 0)
                    .Do("item_scan_rejected", "reject scan, keep state",
                        StationActions.LogScanRejected)
                    .GoTo("picking")

                .On("qty_entered")
                    .When("operator typed a number",
                          c => c.QtyEntered is > 0)
                    .Do("manual_qty_entered", "apply manual qty",
                        StationActions.ApplyManualQty)
                    .GoTo("picking")

                .On("card_tap")
                    .When("operator tapped a product card",
                          c => !string.IsNullOrWhiteSpace(c.Sku))
                    .Do("card_clicked", "deduct qty from tapped card",
                        StationActions.ApplyCardTap)
                    .GoTo("picking")

                .On("tracking_scan")
                    .When("new tracking scanned mid-session — end current and load new",
                          _ => true)
                    .Do("session_ended", "capture previous tracking for audit trail",
                        StationActions.CapturePreviousTracking)
                    .Do("tracking_scanned", "load matching orders into session",
                        StationActions.LoadOrdersForTracking)
                    .GoTo("order-loaded")

                .On("order_complete")
                    .When("every item on an order is picked",
                          c => c.ItemsRemaining is 0)
                    .Do("order_passed", "save QC Passed",
                        StationActions.SaveQcPassed)
                    .GoTo("passed")

                .On("leave_page")
                    .When("operator navigated away with items remaining",
                          c => c.ItemsRemaining is > 0 or null)
                    .Do("order_held", "persist QC Hold for incomplete orders",
                        StationActions.SaveQcHold)
                    .GoTo("held"))

            // ── passed (terminal) ────────────────────────────────────────────
            .State("passed", s => s
                .On("reset")
                    .When("operator reset a passed order",
                          _ => true)
                    .Do("order_reset", "revert status to To be packed",
                        StationActions.ResetOrder)
                    .GoTo("idle")

                .On("tracking_scan")
                    .When("new tracking scanned after QC Passed — start next session",
                          _ => true)
                    .Do("session_ended", "capture previous tracking for audit trail",
                        StationActions.CapturePreviousTracking)
                    .Do("tracking_scanned", "load matching orders into session",
                        StationActions.LoadOrdersForTracking)
                    .GoTo("order-loaded"))

            // ── held (terminal-for-now) ──────────────────────────────────────
            .State("held", s => s
                .On("tracking_scan")
                    .When("new tracking scanned after QC Hold — start next session",
                          _ => true)
                    .Do("session_ended", "capture previous tracking for audit trail",
                        StationActions.CapturePreviousTracking)
                    .Do("tracking_scanned", "load matching orders into session",
                        StationActions.LoadOrdersForTracking)
                    .GoTo("order-loaded"))

            .Build();
}
