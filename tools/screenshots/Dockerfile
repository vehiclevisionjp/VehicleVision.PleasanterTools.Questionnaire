# 取説用の画面の写しを撮るための入れ物。
#
# **ホストに Playwright とブラウザを入れない**（開発環境の方針）。
#
# **日本語フォントを必ず入れる。** Playwright の公式イメージには日本語フォントが
# 入っておらず、入れ忘れると文字がすべて豆腐（□）になる。しかも撮った画像を
# 目で見るまで気付かないので、**撮る前に機械で確かめる**（lib/tofu.ts）。
FROM mcr.microsoft.com/playwright:v1.62.1-noble

# **字形も指定する。** fonts-noto-cjk は SC / TC / JP / KR を 1 つに束ねており、
# どの地域の面を使うかを指定しないと **SC（簡体字）の字形**が選ばれる。
# 日本語のページでも中国語の字形になり、**豆腐の検査では見つからない**（Issue #144）。
#
# fonts-noto-cjk: Noto Sans CJK（SIL Open Font License 1.1）
# fonts-noto-color-emoji: 絵文字（同上）
RUN apt-get update     && apt-get install -y --no-install-recommends         fonts-noto-cjk         fonts-noto-color-emoji     && rm -rf /var/lib/apt/lists/*     && fc-cache -f

# 日本語の字形を固定する（中身の理由は local.conf 側に書いてある）
COPY fontconfig/local.conf /etc/fonts/local.conf
RUN fc-cache -f

# **指定が効いているかを、ここで落とす。** 効いていなければ image を作らせない。
# 字形の違いは撮った画像を見るまで分からないので、**作る時点で止めるのが一番安い。**
# ⚠️ 面の名前の細部（前置きの語）に依存させないため、照合は「cjk jp」だけに緩めてある
RUN fc-match 'sans-serif:lang=ja' | grep -qi 'cjk jp'     && fc-match 'monospace:lang=ja' | grep -qi 'cjk jp'

WORKDIR /work

COPY package.json package-lock.json* ./
RUN npm install --no-audit --no-fund

COPY . .

CMD ["npx", "playwright", "test"]
