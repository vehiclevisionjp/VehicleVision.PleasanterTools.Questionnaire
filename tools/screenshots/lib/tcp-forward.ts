import { connect, createServer, type Server, type Socket } from 'node:net';

/** 素通しごとの開いている接続。**止めるときに残さず切る。** */
const openSockets = new WeakMap<Server, Set<Socket>>();

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
  const sockets = new Set<Socket>();
  const server = createServer((client) => {
    const upstream = connect(targetPort, targetHost);
    sockets.add(client);
    sockets.add(upstream);
    client.pipe(upstream);
    upstream.pipe(client);

    // **片方が切れたらもう片方も閉じる。** 残すと Chromium の接続が溜まる
    const close = () => {
      client.destroy();
      upstream.destroy();
      sockets.delete(client);
      sockets.delete(upstream);
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

  openSockets.set(server, sockets);
  return server;
}

/**
 * 素通しを止める。
 *
 * ⚠️ **開いている接続を先に切る。** `server.close()` は接続がすべて閉じるまで待つが、
 * Chromium は使い終わった接続も保ったままにするので、切らないと待ち続ける（CI で実際に踏んだ）。
 */
export async function stopForward(server: Server): Promise<void> {
  const closed = new Promise<void>((resolve) => {
    server.close(() => resolve());
  });
  for (const socket of openSockets.get(server) ?? []) {
    socket.destroy();
  }
  await closed;
}
