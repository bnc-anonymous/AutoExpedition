using System;
using System.Globalization;

namespace AutoExpedition;

/// <summary>
/// Turning a value in exalts into the text on screen.
///
/// One place for it because there are two callers - the ground text over a remnant and the prices
/// in the combinations window - and a currency shown one way in one and another way in the other
/// would be worse than either choice on its own.
///
/// Decimal places are per currency rather than one setting, because the currencies are not the same
/// size. A tenth of an exalt is noise; a tenth of a divine is a couple of hundred exalts and very
/// much not.
/// </summary>
internal static class Prices
{
    public static string Text(double exalts, AutoExpeditionSettings settings, Valuation valuation)
    {
        var unit = settings.Display.Prices.PriceIn.Value;
        var converts = valuation.Converts(unit);
        var shown = converts ? unit : "Exalted";

        var value = exalts / valuation.PerExalt(unit);
        var places = Math.Clamp(Decimals(settings, shown), 0, 4);

        return value.ToString("F" + places, CultureInfo.InvariantCulture) + Valuation.Suffix(shown);
    }

    private static int Decimals(AutoExpeditionSettings settings, string unit) => unit switch
    {
        "Divine" => settings.Display.Prices.DivineDecimals.Value,
        "Chaos" => settings.Display.Prices.ChaosDecimals.Value,
        _ => settings.Display.Prices.ExaltedDecimals.Value,
    };
}
