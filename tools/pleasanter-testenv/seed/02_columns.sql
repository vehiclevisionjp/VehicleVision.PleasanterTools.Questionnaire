-- 未確定事項 #1：標準の列が型ごとに何本使えるか。
-- Results / Issues テーブルの実列を型別に数える。**これは実機の DB でしか分からない**
-- （公開リポジトリの App_Data/Definitions/Definition_Column に ClassA〜ClassZ の定義は無い）。
SET NOCOUNT ON;
USE [Implem.Pleasanter];

SELECT
    t.name AS TableName,
    CASE
        WHEN c.name LIKE 'Class%'       THEN 'Class'
        WHEN c.name LIKE 'Num%'         THEN 'Num'
        WHEN c.name LIKE 'Date%'        THEN 'Date'
        WHEN c.name LIKE 'Description%' THEN 'Description'
        WHEN c.name LIKE 'Check%'       THEN 'Check'
        WHEN c.name LIKE 'Attachments%' THEN 'Attachments'
    END AS ColumnType,
    COUNT(*) AS Cnt,
    MIN(c.name) AS FirstName,
    MAX(c.name) AS LastName
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.name IN ('Results', 'Issues')
  AND (c.name LIKE 'Class%' OR c.name LIKE 'Num%' OR c.name LIKE 'Date%'
       OR c.name LIKE 'Description%' OR c.name LIKE 'Check%' OR c.name LIKE 'Attachments%')
GROUP BY t.name,
    CASE
        WHEN c.name LIKE 'Class%'       THEN 'Class'
        WHEN c.name LIKE 'Num%'         THEN 'Num'
        WHEN c.name LIKE 'Date%'        THEN 'Date'
        WHEN c.name LIKE 'Description%' THEN 'Description'
        WHEN c.name LIKE 'Check%'       THEN 'Check'
        WHEN c.name LIKE 'Attachments%' THEN 'Attachments'
    END
ORDER BY TableName, ColumnType;

-- 型ごとの桁も見る（マッピングで値が切られないかの判断に要る）
SELECT TOP 12 t.name AS TableName, c.name AS ColumnName, ty.name AS DataType, c.max_length, c.precision, c.scale
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name = 'Results'
  AND c.name IN ('ClassA','NumA','DateA','DescriptionA','CheckA','AttachmentsA')
ORDER BY c.name;
