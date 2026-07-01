using System.Numerics;
using System.Text;
using GuiEcad.Model;
using GuiEcad.Pdf;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using GuiEcad_App.Commands;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;
// x:Name="Canvas"（CanvasControl）と型名 Canvas が衝突するため、添付プロパティ用に別名を使う。
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    private void OnDevicePanelToggle(object sender, RoutedEventArgs e)
        => SetRightPanelVisible(!_devicePanelVisible);

    private void OnRightPanelToggle(object sender, RoutedEventArgs e)
        => OnDevicePanelToggle(sender, e);

    private const double RightPanelWidth = 220;

    /// <summary>右パネル（機器表／プロパティ）をスライド表示/折りたたみし、トグル表示も同期する。</summary>
    private void SetRightPanelVisible(bool visible)
    {
        _devicePanelVisible = visible;
        DevicePanelMenuItem.Text = visible ? "機器表を隠す" : "機器表を表示";
        if (RightPanelToggleGlyph != null) RightPanelToggleGlyph.Text = visible ? "▶" : "◀";
        if (visible)
        {
            RefreshDevicePanel();
            DevicePanel.Visibility = Visibility.Visible;
            AnimateSize(DevicePanel, isWidth: true, to: RightPanelWidth);
        }
        else
        {
            AnimateSize(DevicePanel, isWidth: true, to: 0,
                done: () => DevicePanel.Visibility = Visibility.Collapsed);
        }
    }

    /// <summary>FrameworkElement の Width/Height を滑らかにアニメーションする（スライド開閉用）。</summary>
    private static void AnimateSize(FrameworkElement el, bool isWidth, double to, Action? done = null)
    {
        double from = isWidth ? el.ActualWidth : el.ActualHeight;
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, isWidth ? "Width" : "Height");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            if (isWidth) el.Width = to; else el.Height = to;   // 終端値を確定
            done?.Invoke();
        };
        sb.Begin();
    }

    private void RefreshDevicePanel()
    {
        if (!_devicePanelVisible) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(string Name, string Label, Sheet Sheet, ElementInstance Elem)>();

        foreach (var sheet in _document.Sheets)
        {
            foreach (var elem in sheet.Elements.OrderBy(e => e.Pos.Row).ThenBy(e => e.Pos.Column))
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;
                if (!seen.Add(elem.DeviceName)) continue;
                string kindLabel = PartResolver.CreatesComponent(elem, _document.Library)
                    ? DeviceKindLabel(PartResolver.ComponentKind(elem, _document.Library))
                    : "記号";
                items.Add((elem.DeviceName, kindLabel, sheet, elem));
            }
        }
        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        DeviceListView.Items.Clear();
        foreach (var (name, label, sheet, elem) in items)
        {
            DeviceListView.Items.Add(new ListViewItem
            {
                Content = $"[{label}]  {name}",
                Tag = (sheet, elem),
                Padding = new Thickness(8, 2, 8, 2),
            });
        }
    }

    // ===== プロパティパネル =====

    private void ShowPropertiesPanel()
    {
        if (!_devicePanelVisible) SetRightPanelVisible(true);
        RightPanelTabView.SelectedIndex = 1;
    }

    private bool _refreshingProps;

    private void RefreshPropertiesPanel()
    {
        _refreshingProps = true;
        // Children.Clear() でパネル内の NumberBox 等がフォーカスを持っていた場合、既定のフォーカス
        // 委譲で親 ScrollViewer（IsTabStop 既定 True）へフォーカスが逃げてしまうのを防ぐため、
        // Clear() 前に Commit系4箇所と同じ退避先へ明示的にフォーカスを移しておく。
        FocusSinkButton.Focus(FocusState.Programmatic);
        PropertiesPanel.Children.Clear();

        if (_selectedImage is ImageInsert img)
        {
            BuildImageProperties(img);
            _refreshingProps = false;
            return;
        }

        if (_selected is null)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "要素を選択してください",
                FontSize = 11,
                Opacity = 0.6,
            });
            _refreshingProps = false;
            return;
        }

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = $"{DeviceKindLabel(_selected.Kind)}  {_selected.DeviceName ?? "—"}",
            FontSize = 12,
        });

        // 一般属性（全要素共通）: 機器名・コメント
        AddGeneralAttributes(_selected);

        // ラベル表示位置（基本パーツのみ。自作パーツは対象外）
        if (_selected.PartId is null)
            AddLabelPositionSelector(_selected);

        if (_selected.Kind == ElementKind.SelectSwitch)
        {
            int pos = _selected.Params.TryGetValue(ParamKeys.Position, out var ps) &&
                      int.TryParse(ps, out int n) ? n : 0;
            PropertiesPanel.Children.Add(new TextBlock { Text = "ノッチ位置", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var nb = new NumberBox
            {
                Value = pos,
                Minimum = 0,
                Maximum = 99,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            nb.ValueChanged += OnPositionBoxChanged;
            PropertiesPanel.Children.Add(nb);
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "同名接点に異なる番号を設定し、テストモードでクリックして切り替えます",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        else if (_selected.Kind is ElementKind.Timer
            or ElementKind.TimerContactNO or ElementKind.TimerContactNC
            or ElementKind.TimerInstantContactNO or ElementKind.TimerInstantContactNC)
        {
            // 設定時間はタイマコイル(ElementKind.Timer)が保持するのが基本。接点(限時/瞬時)を選択したときは
            // 同名のタイマコイルがあればそれを、無ければ選択中の接点自身を対象にする（接点側からも必ず設定可能に）。
            var target = _selected.Kind == ElementKind.Timer
                ? _selected
                : _sheet.Elements.FirstOrDefault(e => e.Kind == ElementKind.Timer
                    && !string.IsNullOrEmpty(_selected.DeviceName)
                    && e.DeviceName == _selected.DeviceName) ?? _selected;

            double setpoint = target.Params.TryGetValue(ParamKeys.Setpoint, out var sp) &&
                double.TryParse(sp, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
            PropertiesPanel.Children.Add(new TextBlock { Text = "設定時間 (秒)", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var nb = new NumberBox
            {
                Value = Math.Round(setpoint),
                Minimum = 0,
                Maximum = 9999,
                SmallChange = 1,
                LargeChange = 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // 0〜10 秒のスライダー（手早い設定用・1秒刻み）。NumberBox（数値入力）と双方向同期。
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 10,
                StepFrequency = 1,
                Value = Math.Clamp(Math.Round(setpoint), 0, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 0),
            };
            nb.ValueChanged += (_, a) =>               // NumberBox: コマンド実行＋スライダー追従
            {
                if (_refreshingProps || double.IsNaN(a.NewValue)) return;
                _refreshingProps = true; slider.Value = Math.Clamp(a.NewValue, 0, 10); _refreshingProps = false;
                CommitSetpoint(target, a.NewValue);
            };
            slider.ValueChanged += (_, a) =>           // スライダー操作で NumberBox とコマンドを更新
            {
                if (_refreshingProps) return;
                _refreshingProps = true; nb.Value = a.NewValue; _refreshingProps = false;
                CommitSetpoint(target, a.NewValue);
            };
            PropertiesPanel.Children.Add(nb);
            PropertiesPanel.Children.Add(slider);
        }
        else if (_selected.Kind == ElementKind.Lamp)
        {
            var lampElem = _selected;
            string color = lampElem.Params.TryGetValue(ParamKeys.LampColor, out var c) ? c : "";
            PropertiesPanel.Children.Add(new TextBlock { Text = "ランプ色（中央表示）", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var tb = new TextBox
            {
                Text = color,
                FontSize = 12,
                MaxLength = 4,
                PlaceholderText = "例: R / G / 赤",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            tb.LostFocus += (_, _) => CommitLampColor(lampElem, tb.Text);
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { CommitLampColor(lampElem, tb.Text); e.Handled = true; }
            };
            PropertiesPanel.Children.Add(tb);
        }
        else if (_selected.Kind == ElementKind.Breaker3P)
        {
            var breakerElem = _selected;
            string cur = breakerElem.Params.TryGetValue(ParamKeys.Type, out var t) && !string.IsNullOrEmpty(t)
                ? t : "NFB";
            PropertiesPanel.Children.Add(new TextBlock { Text = "ブレーカ種別", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12,
            };
            foreach (var name in BreakerTypes)
                combo.Items.Add(new ComboBoxItem { Content = name });
            int idx = Array.IndexOf(BreakerTypes, cur);
            combo.SelectedIndex = idx >= 0 ? idx : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Content is string sel)
                    CommitBreakerType(breakerElem, sel);
            };
            PropertiesPanel.Children.Add(combo);
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "NFB/MCCB は同形・ELB は漏電テストボタン印付き。記号脇にラベル表示。",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        // 要素を選択したら右パネルを表示しプロパティタブへ切替（プロパティ編集をすぐ可能に）
        ShowPropertiesPanel();

        _refreshingProps = false;
    }

    private void OnPositionBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshingProps || _selected is null || double.IsNaN(args.NewValue)) return;
        _history.Execute(new SetParamCommand(_sheet, _selected, ParamKeys.Position, ((int)args.NewValue).ToString()));
        Canvas.Invalidate();
    }

    // 設定時間（秒）を Undo/Redo 対応で確定。1秒単位に丸める。NumberBox・スライダー双方から呼ぶ。
    private void CommitSetpoint(ElementInstance target, double value)
    {
        double secs = Math.Round(value);
        _history.Execute(new SetParamCommand(_sheet, target, ParamKeys.Setpoint,
            secs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Canvas.Invalidate();
    }

    // ===== 画像プロパティ =====

    private void BuildImageProperties(ImageInsert img)
    {
        PropertiesPanel.Children.Add(new TextBlock { Text = "画像", FontSize = 12 });
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(img.FilePath),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(0, 4, 0, 8),
        });

        var tracingCheck = new CheckBox
        {
            Content = "トレース用下絵（画面のみ・PDF出力対象外）",
            IsChecked = img.IsTracingOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        tracingCheck.Checked += (_, _) => CommitImageTracingOnly(img, true);
        tracingCheck.Unchecked += (_, _) => CommitImageTracingOnly(img, false);
        PropertiesPanel.Children.Add(tracingCheck);

        PropertiesPanel.Children.Add(new TextBlock { Text = "幅 (mm)", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var widthBox = new NumberBox
        {
            Value = Math.Round(img.WidthMm, 1),
            Minimum = 1,
            Maximum = 2000,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        PropertiesPanel.Children.Add(widthBox);

        PropertiesPanel.Children.Add(new TextBlock { Text = "高さ (mm)", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var heightBox = new NumberBox
        {
            Value = Math.Round(img.HeightMm, 1),
            Minimum = 1,
            Maximum = 2000,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        PropertiesPanel.Children.Add(heightBox);

        widthBox.ValueChanged += (_, a) =>
        {
            if (_refreshingProps || double.IsNaN(a.NewValue)) return;
            CommitImageResize(img, a.NewValue, img.HeightMm);
        };
        heightBox.ValueChanged += (_, a) =>
        {
            if (_refreshingProps || double.IsNaN(a.NewValue)) return;
            CommitImageResize(img, img.WidthMm, a.NewValue);
        };
    }

    private void CommitImageTracingOnly(ImageInsert img, bool value)
    {
        if (_refreshingProps || img.IsTracingOnly == value) return;
        _history.Execute(new SetImageTracingOnlyCommand(_sheet, img, img.IsTracingOnly, value));
        Canvas.Invalidate();
    }

    private void CommitImageResize(ImageInsert img, double newW, double newH)
    {
        double ow = img.WidthMm, oh = img.HeightMm;
        if (Math.Abs(newW - ow) < 0.05 && Math.Abs(newH - oh) < 0.05) return;
        _history.Execute(new ResizeImageCommand(_sheet, img, ow, oh, newW, newH));
        Canvas.Invalidate();
    }

    private void CommitLampColor(ElementInstance elem, string text)
    {
        if (_refreshingProps) return;
        string val = text.Trim();
        string cur = elem.Params.TryGetValue(ParamKeys.LampColor, out var c) ? c : "";
        if (val == cur) return;
        _history.Execute(new SetParamCommand(_sheet, elem, ParamKeys.LampColor, val));
        Canvas.Invalidate();
    }

    // 主回路ブレーカの種別（記号は同形・ELB のみテストボタン印。Params["Type"] 未設定時は NFB 扱い）。
    private static readonly string[] BreakerTypes = { "NFB", "MCCB", "ELB" };

    private void CommitBreakerType(ElementInstance elem, string type)
    {
        if (_refreshingProps) return;
        string cur = elem.Params.TryGetValue(ParamKeys.Type, out var t) && !string.IsNullOrEmpty(t) ? t : "NFB";
        if (type == cur) return;
        _history.Execute(new SetParamCommand(_sheet, elem, ParamKeys.Type, type));
        Canvas.Invalidate();
    }


    /// <summary>機器名・コメントの一般属性編集欄をプロパティパネルへ追加する（全要素共通）。</summary>
    private void AddGeneralAttributes(ElementInstance elem)
    {
        PropertiesPanel.Children.Add(new TextBlock { Text = "機器名", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var nameBox = new TextBox
        {
            Text = elem.DeviceName ?? string.Empty,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        nameBox.LostFocus += (_, _) => CommitPropDeviceName(elem, nameBox.Text);
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { CommitPropDeviceName(elem, nameBox.Text); e.Handled = true; }
        };
        PropertiesPanel.Children.Add(nameBox);

        PropertiesPanel.Children.Add(new TextBlock { Text = "コメント", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var commentBox = new TextBox
        {
            Text = elem.Comment ?? string.Empty,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        commentBox.LostFocus += (_, _) => CommitPropComment(elem, commentBox.Text);
        commentBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { CommitPropComment(elem, commentBox.Text); e.Handled = true; }
        };
        PropertiesPanel.Children.Add(commentBox);
    }

    private void CommitPropDeviceName(ElementInstance elem, string text)
    {
        if (_refreshingProps) return;
        string newName = text.Trim();
        if (newName == (elem.DeviceName ?? string.Empty)) return;
        _history.Execute(new RenameDeviceCommand(_sheet, elem, elem.DeviceName,
            string.IsNullOrEmpty(newName) ? null : newName));
        RefreshDevicePanel();
        Canvas.Invalidate();
    }

    private void CommitPropComment(ElementInstance elem, string text)
    {
        if (_refreshingProps) return;
        string newComment = text.Trim();
        if (newComment == (elem.Comment ?? string.Empty)) return;
        _history.Execute(new SetCommentCommand(_sheet, elem, elem.Comment,
            string.IsNullOrEmpty(newComment) ? null : newComment));
        Canvas.Invalidate();
    }

    /// <summary>ラベル（機器名・コメント）の高さオフセット調整を追加する（正で上へ、密集時の重なり回避）。</summary>
    private void AddLabelPositionSelector(ElementInstance elem)
    {
        double dy = elem.Params.TryGetValue(ParamKeys.LabelDy, out var v) &&
            double.TryParse(v, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double d)
            ? d : ElementCatalog.DefaultLabelDy(elem.Kind);   // 未設定は種別既定値を表示
        PropertiesPanel.Children.Add(new TextBlock { Text = "ラベル高さ調整 (mm・正で上)", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var nb = new NumberBox
        {
            Value = dy,
            Minimum = -20,
            Maximum = 20,
            SmallChange = 0.5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        nb.ValueChanged += OnLabelDyChanged;
        PropertiesPanel.Children.Add(nb);
    }

    private void OnLabelDyChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshingProps || _selected is null || double.IsNaN(args.NewValue)) return;
        _history.Execute(new SetParamCommand(_sheet, _selected, ParamKeys.LabelDy,
            args.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Canvas.Invalidate();
    }

    private void OnDeviceListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceListView.SelectedItem is not ListViewItem item) return;
        if (item.Tag is not (Sheet sheet, ElementInstance elem)) return;
        DeviceListView.SelectedItem = null;   // 選択をリセット（次回の同一機器クリックを有効にする）
        SwitchToSheet(sheet);
        SelectElement(elem);
        RefreshPropertiesPanel();
        Canvas.Invalidate();
    }

    private static string DeviceKindLabel(ElementKind kind) => kind switch
    {
        ElementKind.Coil or ElementKind.Timer => "コイル",
        ElementKind.ContactNO or ElementKind.ContactNC => "接点",
        ElementKind.PushButtonNO or ElementKind.PushButtonNC => "押釦",
        ElementKind.EmergencyStop => "非停",
        ElementKind.ThermalOverload => "OL",
        ElementKind.TimerContactNO or ElementKind.TimerContactNC => "タイマ",
        ElementKind.TimerInstantContactNO or ElementKind.TimerInstantContactNC => "瞬時",
        ElementKind.SelectSwitch => "SS",
        ElementKind.Lamp => "表示灯",
        ElementKind.Terminal => "端子",
        ElementKind.Motor => "モータ",
        ElementKind.Breaker3P => "ブレーカ",
        ElementKind.ContactorMain3P => "MC主接点",
        ElementKind.ThermalOverload3P => "OL(3P)",
        _ => "?",
    };

    // ===== シート管理（複数シート切替） =====

}
