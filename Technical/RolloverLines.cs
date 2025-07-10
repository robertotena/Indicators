using OFT.Localization;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
using OFT.Rendering.Settings;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Utils.Common;

namespace ATAS.Indicators.Technical;

[DisplayName("Rollover Lines")]
public class RolloverLines : Indicator
{
    #region Fields

    private readonly ConcurrentDictionary<int, (string, DateTime)> _barContracts = [];

    private ContractRolloversDescription? _rollovers;
    private int _lastBar = -1;
    private int _loading;
    private bool _isInitialized;
    private int _barUnderMouse;
    private Point _lastMousePosition;

    private FilterEnum<RolloverType> _rolloverType;

    #endregion

    #region Properties

    #region Settings

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.RolloverType), GroupName = nameof(Strings.Settings))]
    public FilterEnum<RolloverType> RolloverType 
    { 
        get => _rolloverType;
        set => SetTrackedProperty(ref _rolloverType, value, (name) =>
        {
            if (name == nameof(RolloverType.Value))
                SetRolloversAsync().ObserveException();
        });
    }

    #endregion

    #region Drawing

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.LineSettings), GroupName = nameof(Strings.Drawing),
       Description = nameof(Strings.LineSettingsDescription))]
    public PenSettings LineSettings { get; set; }

    #endregion

    #region Labels

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowLabelsOfFinishedLines), GroupName = nameof(Strings.Label),
     Description = nameof(Strings.ShowLabelsOfFinishedLinesDescription))]
    public bool ShowLabels { get; set; } = true;

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.Font), GroupName = nameof(Strings.Label),
       Description = nameof(Strings.FontSettingDescription))]
    public FontSetting FontSetting { get; set; }

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.OffsetX), GroupName = nameof(Strings.Label),
    Description = nameof(Strings.LabelOffsetXDescription))]
    public int LabelOffsetX { get; set; } = 5;

    [Range(0, int.MaxValue)]
    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.OffsetY), GroupName = nameof(Strings.Label),
    Description = nameof(Strings.LabelOffsetYDescription))]
    public int LabelOffsetY { get; set; } = 5;

    #endregion

    #endregion

    #region ctor

    public RolloverLines() : base(true)
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DenyToChangePanel = true;

        var data = DataSeries[0] as ValueDataSeries;
        data.IsHidden = true;
        data.ShowZeroValue = false;

        FontSetting = new("Segoe UI", 9);
        LineSettings = new()
        {
            Color = CrossColors.Red,
            Width = 2,
            LineDashStyle = LineDashStyle.Dash
        };

        RolloverType = new FilterEnum<RolloverType>(false) { Enabled = true };
    }

    #endregion

    #region Protected methods

    protected override void OnInitialize()
    {
        _isInitialized = true;
        SetRolloversAsync().ObserveException();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == _lastBar) 
            return;

        _lastBar = bar;

        if (IsNewSession(bar) && bar == CurrentBar - 1) 
            SetRolloversAsync().ObserveException();
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo is null)
            return;

        DrawRollovers(context);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        _barUnderMouse = ChartInfo.MouseLocationInfo.BarBelowMouse;
        _lastMousePosition = ChartInfo.MouseLocationInfo.LastPosition;

        return base.ProcessMouseMove(e);
    }

    #endregion

    #region Private methods

    private async Task SetRolloversAsync()
    {
        if (!_isInitialized || Interlocked.CompareExchange(ref _loading, 1, 0) != 0)
            return;

        try
        {
            RolloverType.SetEnabled(false);
            _rollovers = await DataProvider?.OnlineDataProvider?.GetRolloversAsync(GetCandle(0).Time, GetCandle(CurrentBar - 1).LastTime, RolloverType.Value);

            if (_rollovers is null || _rollovers.Rollovers.Length == 0)
                return;

            _barContracts.Clear();
            var index = 0;

            for (int bar = 0; bar < CurrentBar; bar++)
            {
                var candle = GetCandle(bar);
                var time1 = candle.Time;
                var time2 = bar == CurrentBar - 1 ? candle.LastTime : GetCandle(bar + 1).Time;

                for (var i = index; i < _rollovers.Rollovers.Length; i++) 
                {
                    var (code, rollover) = _rollovers.Rollovers[i];

                    if (rollover >= time1 && rollover <= time2)
                    {
                        _barContracts[bar] = (code, rollover);
                        index++;
                        break;
                    }
                }
            }

            RedrawChart();
        }
        finally 
        {
            Interlocked.Exchange(ref _loading, 0);
            DoActionInGuiThread(() => RolloverType.SetEnabled(true));
        }   
    }

    private void DrawRollovers(RenderContext context)
    {
        if (_rollovers is null)
            return;

        for (var bar = FirstVisibleBarNumber; bar <= LastVisibleBarNumber; bar++) 
        {
            if (!CheckBar(bar) || !_barContracts.TryGetValue(bar, out (string, DateTime) item))
                continue;

            var x = ChartInfo.GetXByBar(bar, false);
            var top = ChartInfo.Region.Top;
            var bottom = ChartInfo.Region.Bottom;

            context.DrawLine(LineSettings.RenderObject, x, top, x, bottom);
            
            if (ShowLabels)
            {
                string text;

                if (ToDrawTimeLabel(bar, x))
                {
                    var time = item.Item2.AddHours(InstrumentInfo?.TimeZone ?? 0);
                    text = $"{item.Item1} ({time:dd.MM.yyyy HH:mm})";
                }
                else
                    text = item.Item1;

                var xPosition = x + LabelOffsetX;
                var yPosition = top + LabelOffsetY;
                context.DrawString(text, FontSetting.RenderObject, LineSettings.Color.Convert(), xPosition, yPosition);
            }
        }
    }

    private bool ToDrawTimeLabel(int bar, int x)
    {
        if (ChartInfo?.PriceChartContainer.BarsWidth > 5)
            return bar == _barUnderMouse;

        var shift = 5;
        return _lastMousePosition.X >= x - shift && _lastMousePosition.X <= x + shift;
    }

    private bool CheckBar(int bar)
    {
        return bar >= 0 && bar < CurrentBar;
    }

    #endregion
}
