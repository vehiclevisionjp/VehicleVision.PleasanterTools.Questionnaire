-- シングルサインオン（Issue #464）の端から端までの試験で使う Pleasanter の利用者を作る（Issue #470）。
--
-- | ログイン ID | パスワード | 2 要素 |
-- | --- | --- | --- |
-- | sso-e2e-plain | SsoE2e#Plain1 | なし |
-- | sso-e2e-mail  | SsoE2e#Mail1  | メールのワンタイムパスワード |
--
-- **何度流してもよい。** 居なければ作り、居ればパスワードと状態を初期値へ戻す。
--
-- **パスワードは SHA-512 の 16 進（小文字）で持つ。** Pleasanter 1.5.8.1 は
-- Users_Password を Sha512Cng()（UTF-8 のバイト列の SHA-512 を小文字の 16 進にしたもの。
-- 塩は無い）で変換して Users.Password とそのまま比べる
-- （Models/Users/UserModel.cs の SetByForm と GetByCredentials、
-- Implem.Libraries/Utilities/Encryptions.cs）。パスワードは ASCII だけなので、
-- varchar のバイト列と UTF-8 のバイト列は一致する。
--
-- **PasswordExpirationTime を NULL にする。** 値があると初回のログインで
-- パスワードの変更を求められ、試験が先へ進めない（実測）。
--
-- **2 要素は sso-e2e-mail にだけ立てる。** Parameters/Security.json の
-- SecondaryAuthentication.Mode を DefaultDisable にしてあり、
-- EnableSecondaryAuthentication を立てた利用者にだけ効く
-- （Models/Users/UserModel.cs の EnabledSecondaryAuthentication）。
-- **メールアドレスは登録しない。** 登録が無ければ Pleasanter はメールを送らず、
-- コードを Users.SecondaryAuthenticationCode へ平文で残すだけになる。試験はそこから読む。
--
-- **この利用者とパスワードは検証環境専用。** 本番の Pleasanter に対して実行しないこと。
SET NOCOUNT ON;
USE [Implem.Pleasanter];

DECLARE @Users TABLE (
    LoginId nvarchar(256) NOT NULL,
    Name nvarchar(256) NOT NULL,
    PlainPassword varchar(64) NOT NULL,
    EnableSecondaryAuthentication bit NOT NULL);

INSERT INTO @Users VALUES
    (N'sso-e2e-plain', N'SSO E2E Plain', 'SsoE2e#Plain1', 0),
    (N'sso-e2e-mail', N'SSO E2E Mail', 'SsoE2e#Mail1', 1);

DECLARE @TenantId int = (SELECT [TenantId] FROM [Users] WHERE [LoginId] = N'Administrator');

IF @TenantId IS NULL
BEGIN
    RAISERROR('Administrator が見つからない。CodeDefiner が失敗している可能性がある。', 16, 1);
    RETURN;
END

UPDATE target SET
    [Name] = source.[Name],
    [Password] = LOWER(CONVERT(nvarchar(128), HASHBYTES('SHA2_512', source.[PlainPassword]), 2)),
    [PasswordExpirationTime] = NULL,
    [EnableSecondaryAuthentication] = source.[EnableSecondaryAuthentication],
    [DisableSecondaryAuthentication] = 0,
    [SecondaryAuthenticationCode] = NULL,
    [SecondaryAuthenticationCodeExpirationTime] = NULL,
    [Disabled] = 0,
    [Lockout] = 0,
    [LockoutCounter] = 0
FROM [Users] AS target
INNER JOIN @Users AS source ON target.[LoginId] = source.[LoginId];

INSERT INTO [Users] (
    [TenantId], [LoginId], [Name], [Password], [Language], [TimeZone],
    [PasswordExpirationTime], [EnableSecondaryAuthentication], [Creator], [Updator])
SELECT
    @TenantId,
    source.[LoginId],
    source.[Name],
    LOWER(CONVERT(nvarchar(128), HASHBYTES('SHA2_512', source.[PlainPassword]), 2)),
    N'ja',
    N'Asia/Tokyo',
    NULL,
    source.[EnableSecondaryAuthentication],
    1,
    1
FROM @Users AS source
WHERE NOT EXISTS (SELECT 1 FROM [Users] WHERE [LoginId] = source.[LoginId]);

SELECT [UserId], [LoginId], [EnableSecondaryAuthentication], [PasswordExpirationTime]
FROM [Users]
WHERE [LoginId] IN (SELECT [LoginId] FROM @Users);
