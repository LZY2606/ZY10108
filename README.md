# Source Map Chain Auditor

本仓库是一个完全本地运行的 .NET 10 系统，用于解码、校验、追踪和修订多级 Source Map v3 链。它不依赖云端账号或外部数据库。

## 运行

```bash
dotnet restore
dotnet test --nologo
dotnet run --project src/Web --urls http://127.0.0.1:5228
```

打开 `http://127.0.0.1:5228` 后可查看最终结论、每级输入、指纹、规则版本、候选修订与事件顺序。

## 持久化格式

数据保存在 `src/Web/data/workspace.json`，写入时先落临时文件再原子替换。顶层格式为：

- `schemaVersion`：当前为 `1`；未知大版本拒绝读取。
- `workspaceId`：本地工作区标识。
- `state`：业务状态机状态，包括 `draft`、`analyzing`、`revision-open`、`publishing`、`published`、`failed`。
- `ruleVersion`：固定为 `source-map-chain/v1`。
- `stages`：按一次批量导入的提交顺序保存原始输出和原始 map JSON。
- `revisions`：候选修订；它们只参与候选组合，不会改写 `stages[*].mapJson`。
- `publications`：发布记录，包含每级输出/map/`sourcesContent` 指纹和确定摘要。
- `events`：带递增序号与稳定幂等键的业务事件。
- `jobs`：可恢复后台作业；完成事件只按幂等键记录一次。

## 语义与兼容策略

- 内部统一使用 UTF-16 code unit 列；标准 source map 按 UTF-16 解释，扩展字段 `xColumnEncoding: "unicode-scalars"` 会在入口和出口显式转换，两种列绝不混算。
- `sourceRoot` 参与路径解析；`a/app.js` 与 `b/app.js` 始终是不同来源。
- 输入 BOM 仅在规范化用于行列和指纹计算时移除；CRLF/CR 规范化为 LF。原始导入字符串仍保留在事件前的 stage 记录中，原始 map 不被修订覆盖。
- 无尾换行不再创建空尾行；`mappings` 中跨行重置生成列增量，但段不能跨行；空映射行允许，连续逗号产生空段会被拒绝。
- 生成列必须在同一行单调非递减；source/name/行列越界返回校验错误。
- 缺少中间输出时可以给出不完整链，结果中显式列出断点。
- 一次批量导入在单个存储事务内验证和写入；任一级失败，整批保持原状。
- 发布摘要由规则版本、每级输入指纹和候选修订确定性计算。任何输入或规则版本变化都会改变摘要并使缓存结论失效。
- `schemaVersion=1` 只做向后兼容的附加字段；删除或重命名既有字段需要新大版本。

## 主要 API

- `GET /api/workspace`：状态、结论、指纹、规则版本和事件。
- `POST /api/import`：原子批量导入，需要 `idempotencyKey`。
- `POST /api/trace`：从最终行列回溯。
- `POST /api/reverse`：从原文区间反查每级受影响生成区间。
- `POST /api/revisions` 与 `POST /api/revisions/{id}/compare`：提交候选并比较最早变化。
- `POST /api/revisions/merge`：不相交段可合并；重叠段展示原段、两份修改和下游影响。
- `POST /api/publish`：发布确定摘要，需要 `idempotencyKey`。
- `GET /api/export` 与 `POST /api/export/import`：导出再导入后摘要和查询保持一致。
