using OFT.Localization;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Utils.Common;

namespace ATAS.Indicators.Technical;

[DisplayName("Rollover Lines")]
public class RolloverLines : Indicator
{
    #region Fields

    private readonly ConcurrentDictionary<int, string> _barContracts = [];

    private ContractRolloversDescription? _rollovers;
    private int _lastBar = -1;
    private bool _loading;

    #endregion

    #region Properties

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.LineSettings), GroupName = nameof(Strings.Drawing),
       Description = nameof(Strings.LineSettingsDescription))]
    public PenSettings LineSettings { get; set; }

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.Font), GroupName = nameof(Strings.Drawing),
       Description = nameof(Strings.FontSettingDescription))]
    public FontSetting FontSetting { get; set; }

    [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowLabelsOfFinishedLines), GroupName = nameof(Strings.Drawing),
     Description = nameof(Strings.ShowLabelsOfFinishedLinesDescription))]
    public bool ShowLabels { get; set; } = true;

    #endregion

    #region ctor

    public RolloverLines() : base(true)
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical);
        DenyToChangePanel = true;

        var data = DataSeries[0] as ValueDataSeries;
        data.IsHidden = true;
        data.ShowZeroValue = false;

        FontSetting = new();
        LineSettings = new()
        {
            Color = CrossColors.Red,
            Width = 2,
            LineDashStyle = LineDashStyle.Dash
        };
    }

    #endregion

    #region Protected methods

    protected override void OnRecalculate()
    {
        _barContracts.Clear();
        _lastBar = -1;
       
        SetRolloversAsync().ObserveException();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == _lastBar || bar < CurrentBar - 1) 
            return;

        if(IsNewSession(bar))
            SetRolloversAsync().ObserveException();
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo is null)
            return;

        DrawRollovers(context);
    }

    #endregion

    #region Private methods

    private async Task SetRolloversAsync()
    {
        if (_loading)
            return;

        _loading = true;

        _rollovers = await DataProvider.OnlineDataProvider.GetRolloversAsync(GetCandle(0).Time, GetCandle(CurrentBar - 1).LastTime, RolloverType.Atas);

        _loading = false;

        if (_rollovers is null || _rollovers.Rollovers.Length == 0)
            return;

        for (int bar = 0; bar < CurrentBar; bar++) 
        {
            var candle = GetCandle(bar);
            var time1 = candle.Time;
            var time2 = bar == CurrentBar - 1 ? candle.LastTime : GetCandle(bar + 1).Time;

            foreach (var (Code, Rollover) in _rollovers.Rollovers)
            {
                if (Rollover >= time1 && Rollover <= time2)
                {
                    _barContracts[bar] = Code;
                }
            }
        }

        RedrawChart();
    }

    private void DrawRollovers(RenderContext context)
    {
        if (_rollovers is null)
            return;

        for (var bar = FirstVisibleBarNumber; bar <= LastVisibleBarNumber; bar++) 
        {
            if (!CheckBar(bar) || !_barContracts.TryGetValue(bar, out string code))
                continue;

            var x = ChartInfo.GetXByBar(bar, false);
            var top = ChartInfo.Region.Top;
            var bottom = ChartInfo.Region.Bottom;

            context.DrawLine(LineSettings.RenderObject, x, top, x, bottom);

            if(ShowLabels)
            {
                var shift = 5;
                var text = $"{ParseContractExpiration(code)} (LIQ)";
                context.DrawString(text, FontSetting.RenderObject, LineSettings.Color.Convert(), x + shift, top + 10);
            }
        }
    }

    public static string ParseContractExpiration(string contractCode)
    {
        if (string.IsNullOrWhiteSpace(contractCode) || contractCode.Length < 3)
            return null;

        char monthCode = contractCode[^2];
        char yearCode = contractCode[^1];

        var monthMap = new Dictionary<char, int>
        {
            ['F'] = 1,
            ['G'] = 2,
            ['H'] = 3,
            ['J'] = 4,
            ['K'] = 5,
            ['M'] = 6,
            ['N'] = 7,
            ['Q'] = 8,
            ['U'] = 9,
            ['V'] = 10,
            ['X'] = 11,
            ['Z'] = 12
        };

        if (!monthMap.TryGetValue(monthCode, out int month))
            return null;

        if (!char.IsDigit(yearCode))
            return null;

        int yearShort = yearCode - '0'; 

        return $"{month:00} {yearShort:00}";
    }


    private bool CheckBar(int bar)
    {
        return bar >= 0 && bar < CurrentBar;
    }

    #endregion
}
