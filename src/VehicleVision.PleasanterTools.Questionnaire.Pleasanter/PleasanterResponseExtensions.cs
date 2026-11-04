using Dnc = VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>DotNetClient の <see cref="Dnc.PleasanterResponse"/> を、本アプリの表示・応答の都合で読むための補助。</summary>
public static class PleasanterResponseExtensions
{
    /// <summary>
    /// 失敗の詳細として見せるステータス。HTTP が 200 でも本文の <c>StatusCode</c> が 200 以外で失敗になったときは、
    /// 本文の値を返す（DotNetClient 0.3.0 は <see cref="Dnc.PleasanterResponse.StatusCode"/> を HTTP のままにし、
    /// 本文の値を <see cref="Dnc.PleasanterResponse.ApplicationStatusCode"/> へ分けたため。
    /// 0.2.0 までの本アプリは、本文の値を <c>StatusCode</c> として扱っていた）。
    /// 通信できなかった（応答なし）ときは <c>null</c>。
    /// </summary>
    public static int? DisplayStatusCode(this Dnc.PleasanterResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return !response.IsSuccess && response.StatusCode == 200 && response.ApplicationStatusCode is { } application
            ? application
            : response.StatusCode;
    }
}
