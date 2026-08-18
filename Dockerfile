# syntax=docker/dockerfile:1
#
# 本アプリのコンテナイメージ。
# **開発環境（ホスト）に .NET SDK も node も要求しない**ようにするためのもの。
# 実行は compose.yaml から行う。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 依存の解決だけ先に済ませてレイヤをキャッシュする
COPY global.json Directory.Build.props ./
COPY VehicleVision.PleasanterTools.Questionnaire.slnx ./
COPY src/ src/
RUN dotnet restore src/VehicleVision.PleasanterTools.Questionnaire.Web

RUN dotnet publish src/VehicleVision.PleasanterTools.Questionnaire.Web \
        -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 設定は Pleasanter と同じく App_Data/Parameters/*.json 方式。
# **資格情報の実値は入れない。** 環境変数か Key Vault から読む
COPY App_Data/ App_Data/
COPY --from=build /app/publish .

# **root で動かさない。**
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "VehicleVision.PleasanterTools.Questionnaire.Web.dll"]
