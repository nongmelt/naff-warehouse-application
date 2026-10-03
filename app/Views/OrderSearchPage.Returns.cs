using app.Models;
using app.Services;
using app.Workflows;
using Microsoft.Maui.Controls.Shapes;
using System.Runtime.Versioning;

namespace app.Views;

[SupportedOSPlatform("windows")]
public partial class OrderSearchPage
{
    // ── Returns side panel fields ──────────────────────────────────────────

    private readonly Dictionary<string, int> _returnsReasonCounts = new();
    private readonly Dictionary<string, int> _returnsPlatformCounts = new();
    private readonly Dictionary<string, int> _carrierExpectedCounts = new();
    private readonly Dictionary<string, Entry> _carrierActualEntries = new();

    // ── Returns Action Form ───────────────────────────────────────────────

    private void OnSelectReturnType(object? sender, EventArgs e)
    {
        _returnsActionType = "return";
        _selectedReturnReason = null;

        BtnReturn.Stroke = Color.FromArgb("#dc2626");
        BtnReturn.StrokeThickness = 2;
        BtnReturn.BackgroundColor = Color.FromArgb("#fef2f2");
        BtnPendingPickup.Stroke = Color.FromArgb("#e5e7eb");
        BtnPendingPickup.StrokeThickness = 1;
        BtnPendingPickup.BackgroundColor = Colors.Transparent;

        ReturnsFormIcon.BackgroundColor = Color.FromArgb("#dc2626");
        ReturnsFormIconLabel.Text = "✕";
        ReturnsFormTitle.Text = "Return Details";
        ConfirmReturnBtn.Text = "✕ Confirm Return";
        ConfirmReturnBtn.BackgroundColor = Color.FromArgb("#dc2626");

        ReturnReasonsSection.IsVisible = true;
        PickupReasonsSection.IsVisible = false;
        BuildReturnReasonChips();
    }

    private void OnSelectPendingPickupType(object? sender, EventArgs e)
    {
        _returnsActionType = "pending_pickup";
        _selectedReturnReason = null;

        BtnReturn.Stroke = Color.FromArgb("#e5e7eb");
        BtnReturn.StrokeThickness = 1;
        BtnReturn.BackgroundColor = Colors.Transparent;
        BtnPendingPickup.Stroke = Color.FromArgb("#a21caf");
        BtnPendingPickup.StrokeThickness = 2;
        BtnPendingPickup.BackgroundColor = Color.FromArgb("#fdf4ff");

        ReturnsFormIcon.BackgroundColor = Color.FromArgb("#a21caf");
        ReturnsFormIconLabel.Text = "⏳";
        ReturnsFormTitle.Text = "Pending Pickup";
        ConfirmReturnBtn.Text = "⏳ Mark Pending Pickup";
        ConfirmReturnBtn.BackgroundColor = Color.FromArgb("#a21caf");

        ReturnReasonsSection.IsVisible = false;
        PickupReasonsSection.IsVisible = true;
        BuildReturnReasonChips();
    }

    private void OnReturnBtnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "return")
            BtnReturn.BackgroundColor = Color.FromArgb("#fff5f5");
    }

    private void OnReturnBtnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "return")
            BtnReturn.BackgroundColor = Colors.Transparent;
    }

    private void OnPickupBtnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "pending_pickup")
            BtnPendingPickup.BackgroundColor = Color.FromArgb("#fdf4ff");
    }

    private void OnPickupBtnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_returnsActionType != "pending_pickup")
            BtnPendingPickup.BackgroundColor = Colors.Transparent;
    }

    private void BuildReturnReasonChips()
    {
        var container = _returnsActionType == "return" ? ReturnReasonChips : PickupReasonChips;
        container.Children.Clear();
        var reasons = _returnsActionType == "return" ? _returnReasons : _pickupReasons;

        foreach (var reason in reasons)
        {
            var isSelected = reason == _selectedReturnReason;
            var chip = new Border
            {
                BackgroundColor = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#fef2f2") : Color.FromArgb("#fdf4ff"))
                    : Colors.White,
                Stroke = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf"))
                    : Color.FromArgb("#e5e7eb"),
                StrokeThickness = isSelected ? 2 : 1,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
                Padding = new Thickness(12, 8),
                Margin = new Thickness(0, 0, 8, 8),
            };

            var label = new Label
            {
                Text = reason,
                FontSize = 13,
                FontAttributes = isSelected ? FontAttributes.Bold : FontAttributes.None,
                TextColor = isSelected
                    ? (_returnsActionType == "return" ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"))
                    : Color.FromArgb("#374151"),
                InputTransparent = true,
            };

            chip.Content = label;
            var capturedReason = reason;
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    _selectedReturnReason = capturedReason;
                    BuildReturnReasonChips();
                }),
            });

            // Hover effect
            var chipRef = chip;
            var defaultBg = chip.BackgroundColor;
            var hoverBg = isSelected ? defaultBg : Color.FromArgb("#f9fafb");
            var ptr = new PointerGestureRecognizer();
            ptr.PointerEntered += (_, _) => { if (chipRef.BackgroundColor == defaultBg) chipRef.BackgroundColor = hoverBg; };
            ptr.PointerExited += (_, _) => chipRef.BackgroundColor = defaultBg;
            chip.GestureRecognizers.Add(ptr);

            container.Children.Add(chip);
        }
    }

    private async void OnConfirmReturn(object? sender, EventArgs e)
    {
        if (_selectedReturnReason is null) return;
        var order = Results.FirstOrDefault();
        if (order is null) return;
        var trackingNumber = CurrentTrackingNumber;
        if (trackingNumber is null) return;

        ConfirmReturnBtn.IsEnabled = false;
        var success = await ApiService.CreateReturnRecordAsync(
            trackingNumber, _returnsActionType, _selectedReturnReason,
            ReturnsNotesEditor.Text, order.ShippingOptions, order.Platform,
            _currentOperatorId, AppSettings.ResolvedStationId);
        ConfirmReturnBtn.IsEnabled = true;

        if (success)
        {
            StationEvents.Emit(
                workflowName: "Returns",
                stepId: "return_recorded",
                trigger: "confirm_button",
                trackingNumber: trackingNumber,
                fromState: "order-loaded",
                toState: _returnsActionType == "return" ? "returned" : "pending-pickup",
                stationId: AppSettings.ResolvedStationId,
                @operator: EffectiveOperator,
                sequenceInSession: 0,
                payload: new Dictionary<string, object?>
                {
                    ["recordType"] = _returnsActionType,
                    ["reason"] = _selectedReturnReason,
                    ["shippingOptions"] = order.ShippingOptions,
                    ["platform"] = order.Platform,
                });

            // Track return type for carousel coloring
            _sessionReturnType[_sessionIndex] = _returnsActionType;

            // Update stat pills
            if (_returnsActionType == "return")
            {
                _returnsReturnedCount++;
                _ = AnimateCountLabelAsync(ReturnsReturnedLabel, _returnsReturnedCount.ToString());
            }
            else
            {
                _returnsPendingCount++;
                _ = AnimateCountLabelAsync(ReturnsPendingLabel, _returnsPendingCount.ToString());
            }

            // Show overlay + transition to inline success state
            ShowReturnSuccessCard(trackingNumber, order.Platform, order.ShippingOptions,
                _selectedReturnReason!, _returnsActionType);
            ShowInlineReturnSuccess(trackingNumber, order.Platform, order.ShippingOptions,
                _selectedReturnReason!, _returnsActionType);

            // Update side panel
            TrackReturnForSidePanel(_selectedReturnReason, order.Platform);
            UpdateReturnsSidePanel();

            _selectedReturnReason = null;
            ReturnsNotesEditor.Text = "";
            BuildReturnReasonChips();

            _carouselDirty = true;
            BuildCarouselUI();
        }
    }

    private void OnSkipReturn(object? sender, EventArgs e)
    {
        _selectedReturnReason = null;
        ReturnsNotesEditor.Text = "";
        BuildReturnReasonChips();
    }

    // ── Returns Success ───────────────────────────────────────────────────

    private async void ShowReturnSuccessCard(string tracking, string? platform, string? carrier, string reason, string actionType)
    {
        bool isReturn = actionType == "return";
        var accentColor = isReturn ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf");
        var bgColor = isReturn ? Color.FromArgb("#f0fdf4") : Color.FromArgb("#fdf4ff");
        var titleColor = isReturn ? Color.FromArgb("#166534") : Color.FromArgb("#86198f");

        ReturnSuccessIcon.BackgroundColor = isReturn ? Color.FromArgb("#16a34a") : Color.FromArgb("#a21caf");
        ReturnSuccessIconLabel.Text = isReturn ? "✓" : "⏳";
        ReturnSuccessTitle.Text = isReturn ? "Returned" : "Pending Pickup";
        ReturnSuccessTitle.TextColor = titleColor;
        ReturnSuccessTracking.Text = tracking;
        ReturnSuccessSubtitle.Text = isReturn ? "Ready for next scan..." : "Stays in queue for next carrier visit.";
        ReturnSuccessBar.BackgroundColor = accentColor;

        if (ReturnSuccessOverlay.Children.OfType<Border>().FirstOrDefault() is { } card)
            card.BackgroundColor = bgColor;

        ReturnSuccessTags.Children.Clear();
        void AddTag(string text, Color dotColor, Color textColor)
        {
            var tag = new Border
            {
                BackgroundColor = Colors.White,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(2),
                Content = new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = dotColor, VerticalOptions = LayoutOptions.Center },
                        new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = textColor },
                    }
                }
            };
            ReturnSuccessTags.Children.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var pLower = platform.ToLowerInvariant();
            var pColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
            AddTag(platform, pColor, pColor);
        }
        if (!string.IsNullOrWhiteSpace(carrier))
            AddTag(carrier, Color.FromArgb("#ea580c"), Color.FromArgb("#c2410c"));
        AddTag(reason, accentColor, isReturn ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"));

        ReturnSuccessBar.WidthRequest = 200;
        ReturnSuccessOverlay.Opacity = 0;
        ReturnSuccessOverlay.IsVisible = true;
        await ReturnSuccessOverlay.FadeToAsync(1, 250, Easing.CubicOut);

        var anim = new Animation(v => ReturnSuccessBar.WidthRequest = v, 200, 0);
        anim.Commit(ReturnSuccessBar, "ReturnCountdown", length: 2000, easing: Easing.Linear);
        await Task.Delay(2000);

        if (ReturnSuccessOverlay.IsVisible)
            await DismissReturnSuccessAsync();
    }

    private async void OnReturnSuccessBackdropTapped(object? sender, TappedEventArgs e)
        => await DismissReturnSuccessAsync();

    private async Task DismissReturnSuccessAsync()
    {
        await ReturnSuccessOverlay.FadeToAsync(0, 300, Easing.CubicIn);
        ReturnSuccessOverlay.IsVisible = false;
    }

    private void ShowInlineReturnSuccess(string tracking, string? platform, string? carrier, string reason, string actionType)
    {
        bool isReturn = actionType == "return";
        var accentColor = isReturn ? Color.FromArgb("#dc2626") : Color.FromArgb("#a21caf");

        // Style the card border
        ReturnsSuccessCard.BackgroundColor = isReturn ? Color.FromArgb("#f0fdf4") : Color.FromArgb("#fdf4ff");
        ReturnsSuccessCard.Stroke = isReturn ? Color.FromArgb("#bbf7d0") : Color.FromArgb("#e9d5ff");

        // Icon
        InlineSuccessIcon.BackgroundColor = isReturn ? Color.FromArgb("#16a34a") : Color.FromArgb("#a21caf");
        InlineSuccessIconLabel.Text = isReturn ? "✓" : "⏳";

        // Title
        InlineSuccessTitle.Text = isReturn ? "Returned" : "Pending Pickup";
        InlineSuccessTitle.TextColor = isReturn ? Color.FromArgb("#166534") : Color.FromArgb("#86198f");
        InlineSuccessTracking.Text = tracking;

        // Subtitle
        InlineSuccessSubtitle.Text = isReturn
            ? "Ready for next scan..."
            : "Stays in queue for next carrier visit.";

        // Tags
        InlineSuccessTags.Children.Clear();
        void AddTag(string text, Color dotColor, Color textColor)
        {
            var tag = new Border
            {
                BackgroundColor = Colors.White,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                Padding = new Thickness(8, 4),
                Margin = new Thickness(2),
                Content = new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, Color = dotColor, VerticalOptions = LayoutOptions.Center },
                        new Label { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = textColor },
                    }
                }
            };
            InlineSuccessTags.Children.Add(tag);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var pLower = platform.ToLowerInvariant();
            var pColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#111827"),
                _ => Color.FromArgb("#6b7280"),
            };
            AddTag(platform, pColor, pColor);
        }
        if (!string.IsNullOrWhiteSpace(carrier))
            AddTag(carrier, Color.FromArgb("#ea580c"), Color.FromArgb("#c2410c"));
        AddTag(reason, accentColor, isReturn ? Color.FromArgb("#991b1b") : Color.FromArgb("#86198f"));

        // Toggle visibility: hide form, show success card
        ReturnsActionForm.IsVisible = false;
        ReturnsSuccessCard.IsVisible = true;
    }

    // ── Returns Side Panel ────────────────────────────────────────────────

    private void UpdateReturnsSidePanel()
    {
        SidePanelReturnedCount.Text = _returnsReturnedCount.ToString();
        SidePanelPendingCount.Text = _returnsPendingCount.ToString();
        BuildCarrierCountsPanel();
        BuildReasonBreakdown();
        BuildPlatformBreakdown();
    }

    private void TrackReturnForSidePanel(string? reason, string? platform)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (!_returnsReasonCounts.TryAdd(reason, 1))
                _returnsReasonCounts[reason]++;
        }
        if (!string.IsNullOrWhiteSpace(platform))
        {
            if (!_returnsPlatformCounts.TryAdd(platform, 1))
                _returnsPlatformCounts[platform]++;
        }
    }

    private void BuildReasonBreakdown()
    {
        ReasonBreakdownPanel.Children.Clear();
        if (_returnsReasonCounts.Count == 0) return;

        int maxCount = _returnsReasonCounts.Values.Max();
        Color[] barColors = [Color.FromArgb("#4318B0"), Color.FromArgb("#f97316"),
            Color.FromArgb("#eab308"), Color.FromArgb("#8b5cf6"), Color.FromArgb("#06b6d4")];
        int colorIdx = 0;

        foreach (var (reason, count) in _returnsReasonCounts.OrderByDescending(x => x.Value))
        {
            double pct = maxCount > 0 ? (double)count / maxCount : 0;
            var barColor = barColors[colorIdx % barColors.Length];
            colorIdx++;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(90)),
                    new(GridLength.Star),
                    new(new GridLength(24)),
                },
                ColumnSpacing = 8,
            };

            row.Add(new Label
            {
                Text = reason,
                FontSize = 11,
                TextColor = Color.FromArgb("#374151"),
                HorizontalTextAlignment = TextAlignment.End,
                LineBreakMode = LineBreakMode.TailTruncation,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            var trackGrid = new Grid { HeightRequest = 8 };
            trackGrid.Add(new BoxView
            {
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                CornerRadius = 4,
            });
            trackGrid.Add(new BoxView
            {
                BackgroundColor = barColor,
                CornerRadius = 4,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = pct * 80,
            });
            row.Add(trackGrid, 1);

            row.Add(new Label
            {
                Text = count.ToString(),
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#111827"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

            ReasonBreakdownPanel.Children.Add(row);
        }
    }

    private void BuildCarrierCountsPanel()
    {
        CarrierCountsPanel.Children.Clear();

        // Header row: Expected / Actual labels
        var hdr = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(14)),
                new(GridLength.Star),
                new(new GridLength(56)),
                new(new GridLength(56)),
            },
            ColumnSpacing = 8,
            Padding = new Thickness(0, 0, 0, 4),
        };
        hdr.Add(new Label(), 0);
        hdr.Add(new Label(), 1);
        hdr.Add(new Label
        {
            Text = "EXPECTED",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9ca3af"),
            HorizontalTextAlignment = TextAlignment.Center,
        }, 2);
        hdr.Add(new Label
        {
            Text = "ACTUAL",
            FontSize = 9,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9ca3af"),
            HorizontalTextAlignment = TextAlignment.Center,
        }, 3);
        CarrierCountsPanel.Children.Add(hdr);

        // Collect carriers from sessions
        var carrierCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sessions)
        {
            var carrier = s.Data.FirstOrDefault()?.ShippingOptions;
            if (!string.IsNullOrWhiteSpace(carrier))
            {
                if (!carrierCounts.TryAdd(carrier, 1))
                    carrierCounts[carrier]++;
            }
        }

        // Merge with known expected counts
        foreach (var (carrier, expected) in _carrierExpectedCounts)
        {
            carrierCounts.TryAdd(carrier, 0);
        }

        Color[] dotColors = [Color.FromArgb("#ea580c"), Color.FromArgb("#e11d48"),
            Color.FromArgb("#ca8a04"), Color.FromArgb("#dc2626"), Color.FromArgb("#a21caf")];
        int colorIdx = 0;

        foreach (var (carrier, sessionCount) in carrierCounts.OrderByDescending(x => x.Value))
        {
            var expected = _carrierExpectedCounts.GetValueOrDefault(carrier, sessionCount);
            var dotColor = dotColors[colorIdx % dotColors.Length];
            colorIdx++;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(14)),
                    new(GridLength.Star),
                    new(new GridLength(56)),
                    new(new GridLength(56)),
                },
                ColumnSpacing = 8,
                Padding = new Thickness(0, 4),
            };

            row.Add(new BoxView
            {
                WidthRequest = 8, HeightRequest = 8,
                CornerRadius = 2, Color = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            row.Add(new Label
            {
                Text = carrier,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            }, 1);

            row.Add(new Label
            {
                Text = expected.ToString(),
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#6b7280"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

            var entry = new Entry
            {
                Placeholder = "0",
                FontSize = 13,
                FontFamily = "Consolas",
                FontAttributes = FontAttributes.Bold,
                Keyboard = Keyboard.Numeric,
                HorizontalTextAlignment = TextAlignment.Center,
                HeightRequest = 28,
                BackgroundColor = Colors.White,
            };
            var capturedCarrier = carrier;
            entry.Unfocused += async (_, _) =>
            {
                if (int.TryParse(entry.Text, out var actualCount))
                    await ApiService.UpsertCarrierParcelCountAsync(capturedCarrier, actualCount, _currentOperatorId);
            };

            if (_carrierActualEntries.TryGetValue(carrier, out var existingEntry))
                entry.Text = existingEntry.Text;
            _carrierActualEntries[carrier] = entry;

            row.Add(entry, 3);
            CarrierCountsPanel.Children.Add(row);
        }
    }

    private void BuildPlatformBreakdown()
    {
        PlatformBreakdownPanel.Children.Clear();
        if (_returnsPlatformCounts.Count == 0) return;

        int maxCount = _returnsPlatformCounts.Values.Max();

        foreach (var (platform, count) in _returnsPlatformCounts.OrderByDescending(x => x.Value))
        {
            double pct = maxCount > 0 ? (double)count / maxCount : 0;
            var pLower = platform.ToLowerInvariant();
            var dotColor = pLower switch
            {
                var p when p.Contains("shopee") => Color.FromArgb("#ee4d2d"),
                var p when p.Contains("lazada") => Color.FromArgb("#0f146d"),
                var p when p.Contains("tiktok") => Color.FromArgb("#1a1a2e"),
                _ => Color.FromArgb("#6b7280"),
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(14)),
                    new(GridLength.Star),
                    new(new GridLength(50)),
                    new(new GridLength(24)),
                },
                ColumnSpacing = 8,
            };

            row.Add(new BoxView
            {
                WidthRequest = 8,
                HeightRequest = 8,
                CornerRadius = 2,
                Color = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 0);

            row.Add(new Label
            {
                Text = platform,
                FontSize = 12,
                TextColor = Color.FromArgb("#374151"),
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            var barGrid = new Grid { HeightRequest = 6 };
            barGrid.Add(new BoxView
            {
                BackgroundColor = Color.FromArgb("#f3f4f6"),
                CornerRadius = 3,
            });
            barGrid.Add(new BoxView
            {
                BackgroundColor = dotColor,
                CornerRadius = 3,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = pct * 50,
            });
            row.Add(barGrid, 2);

            row.Add(new Label
            {
                Text = count.ToString(),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                FontFamily = "Consolas",
                TextColor = Color.FromArgb("#111827"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 3);

            PlatformBreakdownPanel.Children.Add(row);
        }
    }
}
