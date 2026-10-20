using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 本アプリの <see cref="SecretProtector"/> を、Core.Data の <see cref="ISecretCipher"/> に合わせる。
/// 暗号の実装と鍵の管理は本アプリに残し、Core.Data（管理者の TOTP の共有鍵）へは窓口だけを渡す。
/// </summary>
/// <remarks>
/// <para>
/// 暗号化の文脈（<c>questionnaire:totp:{管理者 ID}</c>）は Core.Data が組んで渡す。
/// <b>書き込みは、その文脈に束縛した形</b>（<see cref="SecretProtector.Protect(string, string)"/>）で行う。
/// </para>
/// <para>
/// <b>旧の暗号文（文脈を持たない形）も、そのまま読める。</b>旧の <c>AdminUserStore</c> と <c>SecretProtector</c> は
/// 文脈を使っていなかった（暗号文は鍵だけに束縛されていた）ので、移行（M100004）が写した暗号文は、
/// <see cref="SecretProtector.Unprotect(string, string?)"/> が文脈を問わずに復号する。
/// 再び書き込まれたとき（TOTP の登録し直し）から、新しい文脈に束縛した形になる。
/// </para>
/// </remarks>
public sealed class SecretCipherAdapter(SecretProtector protector) : ISecretCipher
{
    public string Protect(string plaintext, string context) => protector.Protect(plaintext, context);

    /// <exception cref="CryptographicException">壊れている・鍵が違う・文脈が違う。</exception>
    public string Unprotect(string protectedValue, string context) =>
        protector.Unprotect(protectedValue, context)
        ?? throw new CryptographicException("秘密を復号できない（鍵・文脈が違う、または壊れている）。");
}
