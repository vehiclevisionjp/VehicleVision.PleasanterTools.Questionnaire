using VehicleVision.PleasanterTools.Core.Web.Testing;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// Core のパスキー契約（P1〜P10・レート制限の共有・監査）を、本体の HTTP パイプラインと SQLite で実行する
/// （Issue #1082。Core の仕様書 3.10.12 の「アプリがやること」⑦）。
/// 仮想認証器（<see cref="VirtualPasskeyAuthenticator"/>）で、登録・ログイン・再認証・改名・削除・復旧まで通す。
/// </summary>
public sealed class PasskeyContractTests : AdminPasskeyContractTests
{
    protected override async Task<IAdminPasskeyContractHost> CreateHostAsync() =>
        await AdminPasskeyContractHost.StartAsync();
}
