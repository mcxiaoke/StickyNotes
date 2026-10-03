# 便签搜索算法优化与多处命中展示设计方案（修订版）

- **日期**：2026-10-03 (GMT+8)
- **状态**：方案已修订核实，待实施（未修改代码）
- **目标模块**：`SearchService.cs` / `SearchHit.cs` / `NotesListViewModel.cs` / `NotesListWindow.xaml` / `SearchServiceTests.cs`

---

## 一、现状与问题分析（代码实证）

### 1.1 问题一：单便签内多次命中仅展示第一处
- **现象**：当同一个关键词在单张便签中出现多次时，搜索结果列表只展示第一次出现的位置，后序的所有命中位置完全不可见，用户无法在列表中预览也无法直接跳转。
- **根因代码实证**：
  在 [`SearchService.cs:78-105`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/SearchService.cs#L78-L105) 中，逐行扫描便签文本时：
  ```csharp
  for (int i = 0; i < lines.Length; i++)
  {
      // ... 找到第一个匹配关键词后 ...
      if (matchedKeywordInLine != null)
      {
          hitLineIndex = i;
          // ... 记录首处坐标 ...
          break; // 直接终止循环，丢弃便签内后续所有命中行！
      }
  }
  ```
  每个便签最终仅生成一个 `SearchHit`，且仅携带首处命中的 `LineNumber`、`CharIndex` 和上下文。

### 1.2 问题二：多词搜索时部分匹配行掩盖了完全匹配行
- **现象**：搜索短语（例如 `Cloudflare R2`）时，如果便签前部某行只包含单词 `Cloudflare`（如第 3 行 `CloudFlare帐号`），而后部某行完全匹配完整短语 `Cloudflare R2`（如第 9 行 `Cloudflare R2 User Token`），搜索结果仅展示前部的 `CloudFlare帐号`，完全匹配短语的行反而未出现。
- **根因代码实证**：
  在 [`SearchService.cs:30-37`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/SearchService.cs#L30-L37) 中：
  1. 关键词收集将完整短语（`Cloudflare R2`）、紧凑词（`CloudflareR2`）与所有单个 Token（`Cloudflare`、`R2`）扁平放入 `sortedKeywords`；
  2. 逐行自上而下扫描时，第 3 行遇到了 Token `Cloudflare`，行内匹配判定成功；
  3. 算法立即 `break` 退出整篇便签的循环；
  4. 导致第 9 行完全匹配完整短语的高质量目标根本没有机会被扫描到，被物理位置靠前的低质量部分匹配行直接掩盖。

---

## 二、核心方案（方案 A 修订版）：短语优先全局排序 + 命中行扁平展开

方案 A 结合了**核心搜索算法的短语优先级重构**、**相邻命中行智能合并**与**扁平多卡片原生键盘导航**，是体验最直观、全键盘交互最流畅、工程改动风险最低的方案。

### 2.1 匹配质量分级（Match Quality Tiering）

对单行（或合并行）内的匹配质量明确定义 3 级梯队：
1. **Tier 1 - 完整短语匹配（Exact Phrase Match）**：
   行内包含用户输入的完整 `trimmed` 短语（或 `compact` 连写词），如包含 `Cloudflare R2`；
2. **Tier 2 - 同行全词匹配（Same-line All Tokens Match）**：
   行内未按连续短语排列，但同一行内同时包含了全部拆分 Tokens（如包含 `use Cloudflare for R2 storage`）；
3. **Tier 3 - 部分词/跨行匹配（Partial / Cross-line Match）**：
   跨行组合匹配场景下，行内仅包含部分 Tokens（如仅包含 `Cloudflare`，而 `R2` 在其他行）。

---

### 2.2 Tier 2 同行全词命中的跳转坐标规约

- **背景**：当同行出现多个分离的 Token（如 `use Cloudflare for R2 storage`）时，若仅记录首个 Token，用户跳转后只能选中 `Cloudflare`，体验不完整。
- **规约**：
  - `CharIndex` = 行内所有命中 Token 的最小绝对起始索引（即首 Token 起点）；
  - `Length` = （所有命中 Token 的最大绝对结束索引）- `CharIndex`（即跨度覆盖到末 Token 结尾）；
- **效果**：用户按 Enter 跳转进便签后，编辑器选区完整高亮选中 `Cloudflare for R2` 整段，视口居中精准对应该语义区间。

---

### 2.3 相邻命中行的就近合并机制（Merge Adjacent Hits）

- **背景**：若第 8 行与第 9 行连续命中，各自取前后各一行（7~9 行与 8~10 行），两张卡片的上下文高度重合，造成视觉冗余。
- **合并规则**：
  - 遍历收集到的命中行列表，若同一便签内两个相邻命中行的行距 `≤ 2`（例如连续行或仅隔 1 行）：
    - **自动合并为 1 张结果卡片**；
    - 逻辑行号记录为区间形式（例如 `DisplayLineNumber = "8-9"`，数值字段以首命中行 `8` 为准）；
    - 上下文窗口取并集（如合并展开为 7~10 行）；
    - 跳转坐标 `CharIndex` 以首个命中行为准，覆盖至合并区间内末个命中词；
  - 若命中行距 `> 2` 行（例如第 3 行与第 18 行），则独立生成两张卡片。

---

### 2.4 全局两级优先级排序策略（解决跨便签掩盖）

- **背景**：若仅在便签内部做 Tier 排序，最新更新但仅含单个分词的便签（Tier 3）仍会排在较早更新但含完整短语的便签（Tier 1）前面，导致全局第 1 条仍然是低质量结果。
- **全局排序规则**：
  1. **Primary Sort（匹配质量 Tier）**：Tier 1（完整短语） > Tier 2（同行全词） > Tier 3（跨行部分词）；
  2. **Secondary Sort（便签时效性）**：同 Tier 下，按便签最新更新时间 `UpdatedAt` 降序；
  3. **Tertiary Sort（单便签内部）**：同一便签内部，按物理行号 `LineNumber` 升序排列。
- **效果**：无论便签多新，包含完整匹配短语的结果项绝对全局排在最顶部，彻底解决低质量项掩盖高质量项。

---

### 2.5 单便签上限控制（Cap）与极端场景兜底

- **单便签上限截断**：
  - 单张便签最多提取前 **3 个**最具代表性的高质量命中项（合并后的卡片数 ≤ 3）；
  - 避免日志便签或长文本便签包含几十个相同词导致列表刷屏；
- **强制保留跨行兜底保护**：
  - 显式保留 [`SearchService.cs:107-114`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/SearchService.cs#L107-L114) 的 `if (hitLineIndex < 0)` 全文兜底逻辑，作为极端分词跨换行场景的防越界与防空列表守卫，禁止移除。

---

### 2.6 徽标计数与心智模型规范

- **解决口径冲突**：
  - `CountTotalMatches`（[`SearchService.cs:222`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/SearchService.cs#L222)）统计的是便签全文中的“总词频（出现次数）”；
  - 结果卡片展示的是“命中行（或合并命中区间）”，两者数值必然不相等；
  - 放弃形如 `(1/3)` 的伪分页序号（避免用户按分母去列表翻第三张卡片却找不到）。
- **统一胶囊文案规约**：
  - 单处命中便签（`TotalMatches == 1`）：胶囊显示 `第 X 行`（合并行为 `第 X-Y 行`）；
  - 多处命中便签（`TotalMatches > 1`）：胶囊显示 `第 X 行 · 共 N 处`（N 为全文总词频 `TotalMatches`）。
- **心智模型说明**：
  - 列表展现为“相关度质量序”，即最匹配的行排在首位，胶囊清晰陈述“当前行位置 + 便签内总出现频次”，语义完全闭环。

---

### 2.7 状态栏文案与 ViewModel 扩展

- **解决文案语义错误**：
  - 原 XAML [`NotesListWindow.xaml:177`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NotesListWindow.xaml#L177) 硬编码为：
    `<Run Text="找到 " /><Run Text="{Binding SearchHitCount, Mode=OneWay}" /><Run Text=" 张匹配便签 (回车或点击直达)" />`
  - 扁平展开后，`SearchHitCount`（卡片数）可能大于实际便签数。
- **改造方案**：
  - 在 `NotesListViewModel` 中扩展并维护两个指标：
    - `SearchHitCount`：结果卡片总数；
    - `SearchNoteCount`：命中的唯一便签数（`hits.Select(h => h.NoteId).Distinct().Count()`）；
  - 状态栏文案动态适配：
    - 当卡片数等于便签数时：`找到 N 条匹配便签 (回车或点击直达)`；
    - 当卡片数大于便签数时：`找到 N 条结果 (来自 M 张便签，回车或点击直达)`。

---

### 2.8 核心优势：纯原生全键盘操作流

在便签管理中心中，用户习惯使用纯键盘完成检索与直达：
1. 搜索框输入 `Cloudflare R2`；
2. 按 `Down` 键，焦点移至列表项 1（即全局最高质量 Tier 1 完全匹配项）；
3. 直接按 `Enter` 键，立即激活便签并精确高亮居中该完全匹配短语；
4. 若用户需要的是次要匹配行（Tier 2 或 Tier 3），在列表内按一次 `Down` 切换至项 2，按 `Enter` 即可直达。
- **稳定性**：100% 契合 WPF 原生 `ListBox` 焦点管理，无需卡片内嵌套二维导航，零焦点冲突风险。

---

## 三、代码影响面与变更清单

| 文件 | 变更性质 | 变更内容要点 |
| :--- | :--- | :--- |
| [`SearchHit.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Models/SearchHit.cs) | 字段微调 | 新增 `string DisplayLineNumber`（如 `"3"` 或 `"8-9"`）与 `int Tier`（匹配等级），保留现有属性向后兼容 |
| [`SearchService.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/SearchService.cs) | 核心算法重构 | 1. 扫描所有行移除 `break`；<br>2. 划分 Tier 1~3 匹配等级；<br>3. 实现 Tier 2 选区跨度覆盖；<br>4. 相邻命中行（≤2 行）就近合并；<br>5. 全局两级排序（Tier 优先，时效次之，行号最后）；<br>6. 单便签 Cap=3 截断并保留兜底路径 |
| [`NotesListViewModel.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/ViewModels/NotesListViewModel.cs) | 属性扩展 | 引入 `SearchNoteCount` 属性，搜索更新时同步计算命中卡片数与去重便签数 |
| [`NotesListWindow.xaml`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NotesListWindow.xaml) | 文本绑定微调 | 1. 行号胶囊绑定更新为 `DisplayLineNumber` 与 `TotalMatches` 逻辑；<br>2. 状态栏文案改用多指标展示；无任何容器/结构级变动 |
| [`WindowManager.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/WindowManager.cs) | **零改动** | `NavigateToHit` 按 `hit.CharIndex` 和 `hit.Length` 定位，天然兼容 |
| [`NoteWindow.xaml.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NoteWindow.xaml.cs) | **零改动** | `JumpToSearchHit` 精准高亮与居中机制天然兼容 |

---

## 四、测试与性能保障计划

### 4.1 现有测试套件兼容性
- 核验了 [`SearchServiceTests.cs:57`](file:///c:/Home/Projects/StickyNotes/tests/StickyNotes.Tests/SearchServiceTests.cs#L57) 的 `Search_MultipleHits_InSameNote_ShouldReturnOneCardWithTotalMatchesCount`：
  - 测试数据为单行文本 `苹果 香蕉 苹果 橘子 苹果`；
  - 按照“同行命中合并为单卡片”的规则，该测试依然返回 1 张卡片且 `TotalMatches=3`，断言完全相符，无意外回归破损。

### 4.2 补充单元测试
1. `Search_MultiLineHits_ShouldProduceMultipleCardsWithinCap`：验证多行分散命中时产出多个卡片，且单便签数量不超过 Cap（3 张）；
2. `Search_PhraseMatch_ShouldRankAheadOf_TokenMatches_CrossNotes`：验证便签 A（仅分词，刚更新）与便签 B（完整短语，较早更新）跨便签时，便签 B 的 Tier 1 卡片全局排在第一位；
3. `Search_AdjacentHitLines_ShouldMergeContextWindow`：验证第 8 行与第 9 行命中时自动合并为单卡片，行号显示 `8-9`，上下文连续展开；
4. `Search_Tier2_ShouldSpanFromFirstTokenToLastToken`：验证同行分词命中（如 `use Cloudflare for R2`）时，`CharIndex` 与 `Length` 完整覆盖首词至尾词；
5. `Search_CrossLineFallback_ShouldNotThrowOrReturnEmpty`：验证跨行组合兜底路径的健壮性。

### 4.3 性能基准（Benchmark）评估
- **复杂度分析**：取消单便签早停 `break` 后，单便签耗时为对文本行的线性遍历，复杂度为 $O(N)$（与现有 `CountTotalMatches` 同阶）。
- **长文本基准测试**：补充 `Benchmark_Search_VeryLongNote_5000Lines` 测试用例，针对 5000 行超长单便签进行 50 次连续检索，断言单次搜索耗时仍稳定在 `< 15ms` 内（远低于产品要求的 30ms 阈值）。

---

## 五、附录：备选方案对比与评估归档

### 方案二：【便签单卡聚合 + 内部展示多段上下文】
- **设计思路**：一张便签固定为一个卡片，右上角标注 `共 3 处匹配`，卡片内垂直排列 2~3 段带行号的迷你摘要片段。
- **缺陷分析**：
  - **严重损害键盘导航**：外层 `ListBoxItem` 只能整体聚焦，键盘上下键无法直接聚焦或按 `Enter` 直达卡片内部的特定片段；
  - 若在卡片内部实现二级焦点（WPF 嵌套 Focus），极易产生丢失焦点或快捷键冲突 Bug；
  - XAML 需重构卡片模板与事件分发，工程复杂度显著增高。

### 方案三：【主列表标明总数 + 便签窗口内 F3 循环定位】
- **设计思路**：主列表保持单卡片（展示 Tier 1 最佳行），按 Enter 进入便签后，通过快捷键 `F3` / `Shift+F3` 在便签内部循环查找。
- **缺陷分析**：
  - 用户在列表页看不到次要命中的上下文，必须进入便签后盲跳；
  - `NoteWindow` 当前没有任何编辑器内查找链组件，传递搜索词、绘制高亮标记、循环定位属于高风险的新增子系统，并非轻量改动。

### 方案四：【卡片内微型翻页器】
- **设计思路**：单卡片，右上角提供翻页切换按钮 `◀ 1/3 ▶`。
- **缺陷分析**：
  - 增加手动微操点击成本，全键盘操作流彻底中断；
  - 仅为了节省卡片数量而牺牲了“输入即见、单键直达”的搜索核心效率。
