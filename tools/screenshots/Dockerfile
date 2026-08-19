# 取説用の画面の写しを撮るための入れ物。
#
# **ホストに Playwright とブラウザを入れない**（開発環境の方針）。
#
# **日本語フォントを必ず入れる。** Playwright の公式イメージには日本語フォントが
# 入っておらず、入れ忘れると文字がすべて豆腐（□）になる。しかも撮った画像を
# 目で見るまで気付かないので、**撮る前に機械で確かめる**（lib/tofu.ts）。
FROM mcr.microsoft.com/playwright:v1.62.1-noble

# fonts-noto-cjk: Noto Sans CJK（SIL Open Font License 1.1）
# fonts-noto-color-emoji: 絵文字（同上）
RUN apt-get update     && apt-get install -y --no-install-recommends         fonts-noto-cjk         fonts-noto-color-emoji     && rm -rf /var/lib/apt/lists/*     && fc-cache -f

WORKDIR /work

COPY package.json package-lock.json* ./
RUN npm install --no-audit --no-fund

COPY . .

CMD ["npx", "playwright", "test"]
