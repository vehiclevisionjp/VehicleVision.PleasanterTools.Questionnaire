using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Questionnaire 固有の役割の解釈と、既存の例外型を維持する。</summary>
internal static class AuthenticationOptionsAdapter
{
    internal static readonly string[] RegisterRoles = Enum.GetNames<AdminRole>();
    internal const string DefaultRegisterRole = nameof(AdminRole.Editor);

    internal static string? ReadRegisterRole(string? value, string key, bool rejectNumeric)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        // SSO は数値を拒否し、SAML は受理する。複合表記は両方で従来どおり解釈する。
        if (!Enum.TryParse<AdminRole>(trimmed, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)
            || (rejectNumeric && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            throw new InvalidOperationException(
                $"{key} に知らない値が入っています: {trimmed}"
                + $"（使えるのは {string.Join(" / ", RegisterRoles)}）");
        }

        return parsed.ToString();
    }

    internal static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (AuthenticationSettingException exception)
        {
            // Core の日本語文言は既存と同一。証明書の元の例外も従来と同じ位置に保つ。
            throw new InvalidOperationException(exception.Message, exception.InnerException);
        }
    }
}
