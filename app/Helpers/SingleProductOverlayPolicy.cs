using System.Collections.Generic;
using app.Models;

namespace app.Helpers;

/// <summary>
/// Decides whether a freshly loaded Order Search result set should
/// automatically open the product image overlay, and for which product.
/// Pure and stateless so the policy is reviewable and unit-testable in
/// isolation from the MAUI view.
/// </summary>
public static class SingleProductOverlayPolicy
{
    /// <summary>
    /// Returns the lone product to auto-open, or <c>null</c> when the result
    /// set does not qualify.
    ///
    /// Qualifies only when ALL of the following hold:
    ///   • no scan is queued (do not interrupt a pending scan),
    ///   • the entire result set contains exactly one product line,
    ///   • that line is a non-bundle single,
    ///   • that line still needs picking (not fully verified).
    /// </summary>
    /// <param name="results">Loaded orders (each with its parsed products).</param>
    /// <param name="hasPendingScans">True if a scan is queued for processing.</param>
    public static ProductItem? PickSoleProduct(
        IEnumerable<PackingList>? results,
        bool hasPendingScans)
    {
        if (hasPendingScans || results is null)
            return null;

        ProductItem? only = null;
        int count = 0;
        foreach (var order in results)
        {
            if (order.ParsedProducts is null)
                continue;
            foreach (var product in order.ParsedProducts)
            {
                count++;
                if (count > 1)
                    return null; // more than one product line — never auto-open
                only = product;
            }
        }

        if (count != 1 || only is null)
            return null;
        if (only.IsBundle)
            return null;          // bundles excluded per spec
        if (only.IsFullyPicked)
            return null;          // already fully verified — skip

        return only;
    }
}
