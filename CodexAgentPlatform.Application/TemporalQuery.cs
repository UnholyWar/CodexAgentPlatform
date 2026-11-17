using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexAgentPlatform.Application;

public sealed record TimeRange(DateTime FromUtc, DateTime UntilUtc);

public static class TemporalQuery
{
    private static readonly string[] TurkishDays = ["pazartesi", "salı", "çarşamba", "perşembe", "cuma", "cumartesi", "pazar"];
    private static readonly string[] EnglishDays = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    public static TimeRange? Parse(string text, DateTime nowUtc, TimeZoneInfo zone)
    {
        var words = text.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        var week = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        if (Regex.IsMatch(words, @"\b(dün|yesterday)\b"))
            return Range(today.AddDays(-1), today, zone);
        if (Regex.IsMatch(words, @"\b(bugün|today)\b"))
            return Range(today, today.AddDays(1), zone);
        if (Regex.IsMatch(words, @"\b(geçen ay|last month)\b"))
        {
            var month = today.AddDays(1 - today.Day);
            return Range(month.AddMonths(-1), month, zone);
        }
        if (Regex.IsMatch(words, @"\b(geçen hafta|last week)\b"))
            return Range(week.AddDays(-7), week, zone);
        for (var i = 0; i < TurkishDays.Length; i++)
        {
            if (!Regex.IsMatch(words, $@"\b(geçen {TurkishDays[i]}|last {EnglishDays[i]})\b"))
                continue;
            var day = week.AddDays(i - 7);
            return Range(day, day.AddDays(1), zone);
        }
        var ago = Regex.Match(words, @"\b(\d{1,2}|bir|iki|üç|dört|beş|one|two|three|four|five)\s+(hafta önce|weeks? ago)\b");
        if (!ago.Success)
            return null;
        var count = ago.Groups[1].Value switch
        {
            "bir" or "one" => 1, "iki" or "two" => 2, "üç" or "three" => 3,
            "dört" or "four" => 4, "beş" or "five" => 5, var value => int.Parse(value, CultureInfo.InvariantCulture)
        };
        return Range(week.AddDays(-7 * count), week.AddDays(7 - 7 * count), zone);
    }

    private static TimeRange Range(DateTime start, DateTime end, TimeZoneInfo zone) =>
        new(ToUtc(start, zone), ToUtc(end, zone));

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
