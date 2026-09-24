import { connect, createServer, type Server } from 'node:net';

/**
 * 容器の中の `127.0.0.1:{listenPort}` を、compose の網の中の別の容器へ素通しする。
 *
 * **Pleasanter と本アプリを、ブラウザから同じホスト名で開くためのもの**（Issue #470）。
 * Pleasanter のログインで入る機能は、ブラウザが Pleasanter の cookie を本アプリへも送ることが前提で、
 * cookie はホスト名ごとにしか届かない（ポートは区別しない）。容器の網の中では
 * `pleasanter:8080` と `questionnaire-app:8080` でホスト名が違うため、そのままでは確かめられない。
 * `localhost:8080`（Pleasanter）と `localhost:8081`（本アプリ）に並べると、手元の検証環境と同じ形になる。
 *
 * **中身には手を入れない。** TCP をそのまま流すだけ（HTTP を読まない）。
 */
export async function startForward(
  listenPort: number,
  targetHost: string,
  targetPort: number,
): Promise<Server> {
  const server = createServer((client) => {
    const upstream = connect(targetPort, targetHost);
    client.pipe(upstream);
    upstream.pipe(client);

    // **片方が切れたらもう片方も閉じる。** 残すと Chromium の接続が溜まる
    const close = () => {
      client.destroy();
      upstream.destroy();
    };
    client.on('error', close);
    upstream.on('error', close);
    client.on('close', close);
    upstream.on('close', close);
  });

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(listenPort, '127.0.0.1', () => {
      server.off('error', reject);
      resolve();
    });
  });

  return server;
}

/** 素通しを止める。 */
export async function stopForward(server: Server): Promise<void> {
  await new Promise<void>((resolve) => {
    server.close(() => resolve());
  });
}
