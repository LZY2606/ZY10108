# Source Map Chain Inspector

本地可验证的多级 source map 组合与审查系统：导入原始源、中间产物、每级映射与最终 bundle，
解码 VLQ 段并沿映射链逐级回溯，支持修订分支、合并冲突展示与确定性摘要发布。
无云端账号、无外部数据库，全部状态持久化在本地 JSON 文件中。

## 运行

```bash
dotnet restore
dotnet test --nologo
dotnet run --project src/Web --urls http://127.0.0.1:5228
```

打开 http://127.0.0.1:5228 ，点「载入演示项目」即可看到三级链
（`src/hello.ts` → `out/hello.js` → `dist/bundle.js`）。

## 结构

- `src/Core` — 领域层：VLQ 编解码、source map 解析、链式组合、校验、修订/合并、
  指纹与确定性摘要、文件存储、业务状态机。
- `src/Web` — ASP.NET Core 最小 API + 原生 JS 单页（`wwwroot`）。
- `tests/Core.Tests` — xUnit 测试（33 个）。

## 核心语义（固定行为）

- **文件身份** = `(sourceRoot, path)`。同名路径在不同 `sourceRoot` 下是不同文件，绝不合并。
- **列语义**：每个文件/映射声明 `utf16` 或 `unicodeScalar`（map 里用 `x_columnSemantics`）。
  两者不一致时报 `ColumnSemanticsMismatch`，绝不混算。
- **BOM**：导入时剥离 U+FEFF 并记录 `hadBom`，指纹按剥离后内容计算。
- **行规则**：按 `\n` 切分；尾部换行不产生幻影行；无尾换行的最后一行仍是合法行。
- **空映射段**：无段的生成行 = 空段列表；仅生成侧的段（1 字段）合法但不产生链。
- **缺中间源**：链标记为不完整，记录断点层级与原因（`missing-source` / `no-map` /
  `unmapped-position`），其余条目照常组合。
- **校验**：生成列同行严格递增、源索引越界、源行/列越界、`sourcesContent` 指纹比对。

## 组合与查询

- 组合从最终层 map 出发逐级 GLB（最大下界）查找，直到第 0 层。
- 正向追踪：`GET /api/projects/{id}/trace?path=..&line=..&col=..`。
- 反向查找：`GET /api/projects/{id}/reverse?level=..&path=..&line=..&col=..&length=..`，
  返回受影响的最终生成区间。
- 发布的组合结果包含每级输入指纹、规则版本与确定性摘要（canonical JSON 的 SHA-256）。
  任何一层内容变化都会改变该层指纹，使组合缓存（按修订 keyed 的结果）失效并需重新组合。

## 修订与合并

- 修订是候选分支：编辑（`segment` 段修正 / `pathRule` 路径规则）只作用于克隆，
  原始 map 永不改写。
- `GET /api/projects/{id}/compare?revisionId=..` 给出与基准的最早变化位置。
- 两个修订触及的段键不相交时可合并；重叠时返回冲突详情：原段、两份修改、
  各自的下游影响（最终生成区间）。

## 持久化格式与兼容策略

- 每个项目一个 JSON 文件：`src/Web/data/{projectId}.json`（可用 `DATA_DIR` 环境变量覆盖）。
- 写入是**一次原子提交**：先写 `{id}.json.tmp` 再 `rename` 覆盖；一次批量操作
  （如多级导入）在内存中全部校验通过后才落盘，失败则文件保持原状。
- 文档内含：层级（文件含指纹/BOM/换行标记、map 原文）、修订、组合结果、
  事件日志（`seq` 单调递增）、幂等操作表（`Idempotency-Key` → 结果）、任务记录。
- **幂等**：所有变更类 API 接受 `Idempotency-Key` 头；重试返回已记录结果，
  不追加事件、不重复入库。后台任务（compose）崩溃后在下次启动时由
  `ResumeIncompleteJobs` 恢复；完成事件按 jobId 去重，不会重复记入结果。
- **导出/导入**：`GET /api/projects/{id}/export` 产生
  `{ formatVersion, exportedAt, project }`；`POST /api/import` 恢复。
  导出再导入后所有查询结果（含摘要）保持一致；重复导入同一项目是幂等 no-op。
- **兼容策略**：`formatVersion` 当前为 1。导入端拒绝高于自身支持的版本
  （报 `UnsupportedFormat`）；同版本内新增字段均为可选并带默认值，
  旧文件可直接被新代码读取。

## API 一览

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/projects` | 创建项目 |
| POST | `/api/projects/{id}/imports` | 原子批量导入层级 |
| POST | `/api/projects/{id}/compose` | 组合（body 可带 `revisionId`） |
| POST | `/api/projects/{id}/publish` | 发布组合结果 |
| GET | `/api/projects/{id}/composition` | 查看组合结果 |
| GET | `/api/projects/{id}/trace` | 正向追踪 |
| GET | `/api/projects/{id}/reverse` | 反向查找 |
| POST | `/api/projects/{id}/revisions` | 创建修订 |
| GET | `/api/projects/{id}/compare` | 与基准比较最早变化 |
| POST | `/api/projects/{id}/merge` | 合并两个修订 |
| GET | `/api/projects/{id}/events` | 事件顺序 |
| GET | `/api/projects/{id}/export` / POST `/api/import` | 导出 / 导入 |
| POST | `/api/demo` | 生成演示项目 |
