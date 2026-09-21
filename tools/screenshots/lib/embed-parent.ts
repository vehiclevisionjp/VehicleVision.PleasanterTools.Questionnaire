import { readFileSync } from 'node:fs';
import { createServer, type Server } from 'node:https';

const certificatePath = process.env['EMBED_PARENT_CERTIFICATE_PATH'] ?? '/https/aspnetapp.pfx';
const certificatePassword =
  process.env['EMBED_PARENT_CERTIFICATE_PASSWORD'] ?? 'Questionnaire#DevCert1';

/** 別生成元の親ページを、検証環境の自己署名証明書で HTTPS 配信する。 */
export async function startEmbedParent(port: number): Promise<Server> {
  const server = createServer(
    {
      pfx: readFileSync(certificatePath),
      passphrase: certificatePassword,
    },
    (_request, response) => {
      response.writeHead(200, {
        'content-type': 'text/html; charset=utf-8',
        'cache-control': 'no-store',
      });
      response.end(parentPage);
    },
  );

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => {
      server.off('error', reject);
      resolve();
    });
  });

  return server;
}

/** 待受中の要求を残さず親ページを閉じる。 */
export async function stopEmbedParent(server: Server): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    server.close((error) => (error ? reject(error) : resolve()));
  });
}

const parentPage = `<!doctype html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <title>埋め込み回答の検証用親ページ</title>
</head>
<body>
  <h1>埋め込み回答の検証用親ページ</h1>
  <script>
    (() => {
      const parameters = new URLSearchParams(window.location.search);
      const iframe = document.createElement('iframe');
      iframe.src = parameters.get('src') ?? '';
      iframe.title = '埋め込んだ回答画面';
      iframe.height = '800';
      iframe.dataset.questionnairePublicId = parameters.get('publicId') ?? '';
      document.body.append(iframe);

      const applicationOrigin = new URL(iframe.src).origin;
      window.addEventListener('message', (event) => {
        const message = event.data;
        if (
          event.origin !== applicationOrigin ||
          event.source !== iframe.contentWindow ||
          message === null ||
          typeof message !== 'object' ||
          Array.isArray(message) ||
          message.type !== 'questionnaire:height' ||
          message.publicId !== iframe.dataset.questionnairePublicId ||
          !Number.isSafeInteger(message.height) ||
          message.height < 0 ||
          message.height > 10000
        ) return;

        iframe.style.height = message.height + 'px';
      });
    })();
  <\/script>
</body>
</html>`;
