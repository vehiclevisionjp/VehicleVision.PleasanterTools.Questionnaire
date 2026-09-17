/** アップロード済み資産を本文へ貼る記法を作る。 */
export function assetMarkup(fileName: string, assetId: string, isImage: boolean): string {
  return isImage
    ? `![${fileName}](asset:${assetId})`
    : `[${fileName}](asset:${assetId})`;
}
