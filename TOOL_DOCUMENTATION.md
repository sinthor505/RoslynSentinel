# RoslynSentinel Tool Documentation

Comprehensive reference for all 320+ refactoring, modernization, analysis, and code generation tools available in RoslynSentinel.

**Generated:** 2026-05-03 21:21:19  
**Total Sources:** 51 (44 engines + 7 tool classes)  
**Total Tools:** 320

---

## Overview

RoslynSentinel provides 320 specialized tools organized across:
- **44 Engine Classes** - Focused refactoring and analysis engines
- **7 Tool Classes** - Sentinel augmentation, generation, quality, and workspace tools

### Document Structure

1. **Index by Category** - Tools organized by functional area
2. **Complete Tool List** - Quick reference of all tools by source
3. **Detailed Documentation** - Full documentation with signatures for each tool

---

## Index by Category

### Analysis & Diagnostics (5 sources, 25 tools)

- **AnalysisEngine** (Engine) - 3 tools

- **DiagnosticEngine** (Engine) - 3 tools

- **DiscoveryEngine** (Engine) - 2 tools

- **HealthOrchestrationEngine** (Engine) - 1 tools

- **SentinelIntelligenceTools** (Tools) - 16 tools


### Code Generation (2 sources, 23 tools)

- **CodeGenerationEngine** (Engine) - 10 tools

- **SentinelGenerationTools** (Tools) - 13 tools


### Modernization (5 sources, 41 tools)

- **ModernLoggingEngine** (Engine) - 1 tools

- **ModernizationEngine** (Engine) - 3 tools

- **ModernizationUpgradeEngine** (Engine) - 3 tools

- **SentinelModernizationTools** (Tools) - 24 tools

- **SyntaxUpgradeEngine** (Engine) - 10 tools


### Performance & Optimization (3 sources, 16 tools)

- **AdvancedLogicEngine** (Engine) - 7 tools

- **AsyncOptimizationEngine** (Engine) - 7 tools

- **LogicOptimizationEngine** (Engine) - 2 tools


### Quality & Style (3 sources, 34 tools)

- **MsToolAugmentEngine** (Engine) - 10 tools

- **SentinelAugmentTools** (Tools) - 10 tools

- **SentinelQualityTools** (Tools) - 14 tools


### Refactoring (7 sources, 112 tools)

- **AdvancedRefactoringEngine** (Engine) - 2 tools

- **GranularRefactoringEngine** (Engine) - 9 tools

- **RefactoringEngine** (Engine) - 34 tools

- **RefinementEngine** (Engine) - 1 tools

- **SentinelRefactoringTools** (Tools) - 61 tools

- **StandardRefactoringEngine** (Engine) - 3 tools

- **StructuralRefinementEngine** (Engine) - 2 tools


### Security & Safety (1 sources, 2 tools)

- **ThreadSafetyEngine** (Engine) - 2 tools


### Testing (1 sources, 4 tools)

- **TestingEngine** (Engine) - 4 tools


### Type & Semantic Analysis (8 sources, 20 tools)

- **CodeFlowEngine** (Engine) - 1 tools

- **CodeSmellAndStyleEngine** (Engine) - 1 tools

- **CodeStyleEngine** (Engine) - 7 tools

- **ControlFlowEngine** (Engine) - 3 tools

- **DependencyEngine** (Engine) - 1 tools

- **DependencyInjectionEngine** (Engine) - 1 tools

- **IDEStyleEngine** (Engine) - 3 tools

- **SymbolNavigationEngine** (Engine) - 3 tools


### Workspace & Utilities (16 sources, 43 tools)

- **AdvancedStructuralEngine** (Engine) - 2 tools

- **ApiAutomationEngine** (Engine) - 1 tools

- **ApiIntegrationEngine** (Engine) - 1 tools

- **ArchitecturalEngine** (Engine) - 1 tools

- **CodeHealingEngine** (Engine) - 2 tools

- **DiffEngine** (Engine) - 1 tools

- **DocumentationEngine** (Engine) - 2 tools

- **ImmutabilityEngine** (Engine) - 1 tools

- **InstrumentationEngine** (Engine) - 3 tools

- **InventoryEngine** (Engine) - 1 tools

- **MappingEngine** (Engine) - 2 tools

- **MetricsEngine** (Engine) - 1 tools

- **ProjectStructureEngine** (Engine) - 2 tools

- **SentinelWorkspaceTools** (Tools) - 19 tools

- **SolutionManagementEngine** (Engine) - 2 tools

- **ValidationEngine** (Engine) - 2 tools



---

## Complete Tool List


### Engines


**AdvancedLogicEngine** (7 tools):

1. `ConvertForEachToForAsync`

2. `ConvertForToForEachAsync`

3. `ConvertIfToSwitchExpressionAsync`

4. `ConvertIfToSwitchStatementAsync`

5. `ConvertStaticToExtensionAsync`

6. `ConvertWhileToForAsync`

7. `ExtensionToStaticAsync`



**AdvancedRefactoringEngine** (2 tools):

8. `OptimizeTaskWaitAsync`

9. `ReplaceStringConcatWithInterpolationAsync`



**AdvancedStructuralEngine** (2 tools):

10. `ConvertAbstractClassToInterfaceAsync`

11. `ReplaceConstructorWithFactoryAsync`



**AnalysisEngine** (3 tools):

12. `FooAsync`

13. `GenerateCallTreeAsync`

14. `GenerateEqualityOverridesAsync`



**ApiAutomationEngine** (1 tools):

15. `GenerateHttpClientForControllerAsync`



**ApiIntegrationEngine** (1 tools):

16. `AddValidationToPocoAsync`



**ArchitecturalEngine** (1 tools):

17. `ConvertToBackgroundServiceAsync`



**AsyncOptimizationEngine** (7 tools):

18. `AddCancellationTokenToMethodAsync`

19. `AddConfigureAwaitFalseAsync`

20. `ConvertToAsyncEnumerableAsync`

21. `GenerateAsyncOverloadAsync`

22. `OptimizeIndependentAwaitsAsync`

23. `OptimizeToValueTaskAsync`

24. `RemoveConfigureAwaitFalseAsync`



**CodeFlowEngine** (1 tools):

25. `ReduceBlockDepthAsync`



**CodeGenerationEngine** (10 tools):

26. `ConvertPropertySafeAsync`

27. `GenerateClassesFromJson`

28. `GenerateConstructorAsync`

29. `GenerateDecoratorClassAsync`

30. `GenerateDefaultConfigJsonAsync`

31. `GenerateFluentBuilderAsync`

32. `GenerateRepositoryInterfaceAsync`

33. `GenerateToStringAsync`

34. `ImplementInterfaceAsync`

35. `InterpolateStringAsync`



**CodeHealingEngine** (2 tools):

36. `AddRetryPolicyAsync`

37. `FixThreadSleepAsync`



**CodeSmellAndStyleEngine** (1 tools):

38. `UseSwitchExpressionAsync`



**CodeStyleEngine** (7 tools):

39. `ConvertPropertyToMethodsAsync`

40. `FixDangerousLockAsync`

41. `SimplifyAllNamesAsync`

42. `SimplifyVerbosityAsync`

43. `UseCollectionExpressionsAsync`

44. `UseIndexFromEndAsync`

45. `UseTimeProviderAsync`



**ControlFlowEngine** (3 tools):

46. `AnalyzeMethodControlFlowAsync`

47. `AnalyzeMethodDataFlowAsync`

48. `AnalyzePathCoverageAsync`



**DependencyEngine** (1 tools):

49. `GetProjectDependenciesAsync`



**DependencyInjectionEngine** (1 tools):

50. `AddDependencyAsync`



**DiagnosticEngine** (3 tools):

51. `GetFileDiagnosticsAsync`

52. `GetProjectDiagnosticsAsync`

53. `GetSolutionDiagnosticsAsync`



**DiffEngine** (1 tools):

54. `ApplyDiff`



**DiscoveryEngine** (2 tools):

55. `FindBestInsertionPointAsync`

56. `PreviewRenameImpactAsync`



**DocumentationEngine** (2 tools):

57. `DocumentPocoFieldsAsync`

58. `GenerateXmlDocumentationStubsAsync`



**GranularRefactoringEngine** (9 tools):

59. `ConvertMethodToIndexerAsync`

60. `InlineFieldAsync`

61. `InlineParameterAsync`

62. `IntroduceFieldAsync`

63. `IntroduceParameterAsync`

64. `IntroduceParameterObjectAsync`

65. `IntroduceVariableAsync`

66. `MoveTypeToOuterScopeAsync`

67. `RunMicroRefactoringAsync`



**HealthOrchestrationEngine** (1 tools):

68. `GenerateComprehensiveHealthReportAsync`



**IDEStyleEngine** (3 tools):

69. `SimplifyMemberAccessAsync`

70. `UseNullPropagationAsync`

71. `UseObjectInitializersAsync`



**ImmutabilityEngine** (1 tools):

72. `MakeClassImmutableAsync`



**InstrumentationEngine** (3 tools):

73. `AddStopwatchDiagnosticsAsync`

74. `AddTryCatchToClassAsync`

75. `AddTryCatchToMethodAsync`



**InventoryEngine** (1 tools):

76. `GetCodeInventoryAsync`



**LogicOptimizationEngine** (2 tools):

77. `AddGuardClausesAsync`

78. `SimplifyBooleanExpressionsAsync`



**MappingEngine** (2 tools):

79. `GenerateMappingAsync`

80. `InvertAssignmentsAsync`



**MetricsEngine** (1 tools):

81. `GetSolutionMetricsAsync`



**ModernLoggingEngine** (1 tools):

82. `ConvertToSourceGeneratedLoggingAsync`



**ModernizationEngine** (3 tools):

83. `ClassToRecordAsync`

84. `ConvertMethodToExpressionBodyAsync`

85. `RecordToClassAsync`



**ModernizationUpgradeEngine** (3 tools):

86. `UpgradePatternMatchingAsync`

87. `UseSpanForParsingAsync`

88. `UseThrowExpressionsAsync`



**MsToolAugmentEngine** (10 tools):

89. `AnalyzeForeachForLinqConversionAsync`

90. `AnalyzeSwitchForPatternConversionAsync`

91. `ConvertStringFormatToInterpolatedSmartAsync`

92. `ConvertSwitchToPatternSafeAsync`

93. `EncapsulateFieldSafeAsync`

94. `ExtractConstantSafeAsync`

95. `FormatDocumentSafeAsync`

96. `GetWorkspaceHealthAsync`

97. `PreviewAddMissingUsingsAsync`

98. `SortAndDeduplicateUsingsAsync`



**ProjectStructureEngine** (2 tools):

99. `FixMismatchedNamespacesAsync`

100. `MoveFileToNamespaceFolderAsync`



**RefactoringEngine** (34 tools):

101. `AddAttributeAsync`

102. `AddBaseTypeAsync`

103. `AddConstructorParameterAsync`

104. `AddEnumValueAsync`

105. `AddFieldAsync`

106. `AddMemberAsync`

107. `AddModifierAsync`

108. `AddPropertyAsync`

109. `AddRemoveParamsAsync`

110. `AddSummaryCommentAsync`

111. `AddUsingDirectiveAsync`

112. `AnalyzeControlFlowAsync`

113. `AnalyzeDataFlowAsync`

114. `ChangeAccessibilityAsync`

115. `ConvertExpressionBodyAsync`

116. `ConvertIndexerToMethodAsync`

117. `ConvertToPrimaryConstructorAsync`

118. `ExtractConstantAsync`

119. `ExtractMethodAsync`

120. `FormatDocumentAsync`

121. `FormatDocumentPreviewAsync`

122. `InsertMemberAfterAsync`

123. `InsertMemberBeforeAsync`

124. `RemoveAttributeAsync`

125. `RemoveBaseTypeAsync`

126. `RemoveMemberAsync`

127. `RemoveModifierAsync`

128. `RenameSymbolAsync`

129. `ReplaceMemberAsync`

130. `SortMembersAsync`

131. `SyncInterfaceToImplementationAsync`

132. `UpdateXmlDocsFromSignatureAsync`

133. `WrapInRegionAsync`

134. `WrapInTryCatchAsync`



**RefinementEngine** (1 tools):

135. `InlineMethodAsync`



**SolutionManagementEngine** (2 tools):

136. `CreateProjectAsync`

137. `SplitProjectByFolderAsync`



**StandardRefactoringEngine** (3 tools):

138. `ConvertMethodToPropertyAsync`

139. `InvertBooleanAsync`

140. `MakeMethodStaticAsync`



**StructuralRefinementEngine** (2 tools):

141. `SafeDeleteSymbolAsync`

142. `SyncTypeAndFilenameAsync`



**SymbolNavigationEngine** (3 tools):

143. `GetCallGraphAsync`

144. `GetReverseCallGraphAsync`

145. `GetSymbolInfoAsync`



**SyntaxUpgradeEngine** (10 tools):

146. `AddBracesAsync`

147. `CleanupImplicitSpansAsync`

148. `ConvertSwitchExpressionToStatementAsync`

149. `ConvertSwitchToExpressionAsync`

150. `UpgradePatternMatchingAsync`

151. `UpgradeToModernGuardsAsync`

152. `UpgradeToPrimaryConstructorAsync`

153. `UseExceptionExpressionsAsync`

154. `UseFieldBackedPropertiesAsync`

155. `UseNameofExpressionAsync`



**TestingEngine** (4 tools):

156. `AddBenchmarkStubAsync`

157. `CalculateComplexityAsync`

158. `GenerateTestScaffoldAsync`

159. `GenerateTestSkeletonAsync`



**ThreadSafetyEngine** (2 tools):

160. `ConvertLockToSemaphoreSlimAsync`

161. `MakeMethodThreadSafeAsync`



**ValidationEngine** (2 tools):

162. `ValidateChangesAsync`

163. `ValidateDiffAsync`



### Tool Classes


**SentinelAugmentTools** (10 tools):

164. `AnalyzeForeachForLinqConversion`

165. `AnalyzeSwitchForPatternConversion`

166. `ConvertStringFormatToInterpolatedSmart`

167. `ConvertSwitchToPatternSafe`

168. `EncapsulateFieldSafe`

169. `ExtractConstantSafe`

170. `FormatDocumentSafe`

171. `GetWorkspaceHealth`

172. `PreviewAddMissingUsings`

173. `SortAndDeduplicateUsings`



**SentinelGenerationTools** (13 tools):

174. `AddValidationToPoco`

175. `ConvertPropertySafe`

176. `GenerateAsyncOverload`

177. `GenerateClassesFromJson`

178. `GenerateConstructor`

179. `GenerateDecoratorClass`

180. `GenerateDefaultConfigJson`

181. `GenerateFluentBuilder`

182. `GenerateHttpClient`

183. `GenerateRepositoryInterface`

184. `GenerateToString`

185. `ImplementInterfaceSafe`

186. `InterpolateStringSafe`



**SentinelIntelligenceTools** (16 tools):

187. `ConvertToBackgroundService`

188. `DocumentPocoFields`

189. `FindBestInsertionPoint`

190. `FixMismatchedNamespaces`

191. `GenerateCallTree`

192. `GenerateEqualityOverrides`

193. `GetBlastRadius`

194. `GetById`

195. `GetCallGraph`

196. `GetCodeInventory`

197. `GetComprehensiveHealthReport`

198. `GetReverseCallGraph`

199. `GetSolutionMetrics`

200. `GetSymbolInfo`

201. `MoveFileToNamespaceFolder`

202. `PreviewRenameImpact`



**SentinelModernizationTools** (24 tools):

203. `AddBraces`

204. `ClassToRecord`

205. `CleanupImplicitSpans`

206. `ConvertStaticToExtension`

207. `ConvertSwitchToExpression`

208. `ConvertToSourceGeneratedLogging`

209. `FixThreadSleep`

210. `MakeClassImmutable`

211. `ModernizeExceptions`

212. `OptimizeIndependentAwaits`

213. `OptimizeToValueTask`

214. `RecordToClass`

215. `SimplifyBooleanExpressions`

216. `SimplifyMemberAccess`

217. `SimplifyVerbosity`

218. `UpgradePatternMatching`

219. `UpgradeThreadSafety`

220. `UpgradeToModernGuards`

221. `UpgradeToPrimaryConstructor`

222. `UpgradeUnboundNameof`

223. `UseExceptionExpressions`

224. `UseFieldBackedProperties`

225. `UseIndexFromEnd`

226. `UseTimeProvider`



**SentinelQualityTools** (14 tools):

227. `AddBenchmarkStub`

228. `AddCancellationTokenToMethod`

229. `AddConfigureAwaitFalse`

230. `AddGuardClauses`

231. `AnalyzeMethodControlFlow`

232. `AnalyzeMethodDataFlow`

233. `AnalyzePathCoverage`

234. `ConvertLockToSemaphoreSlim`

235. `ConvertToAsyncEnumerable`

236. `GenerateTestScaffold`

237. `GenerateTestSkeleton`

238. `GetDiagnosticsSummary`

239. `MakeMethodThreadSafe`

240. `RemoveConfigureAwaitFalse`



**SentinelRefactoringTools** (61 tools):

241. `AddAttribute`

242. `AddBaseType`

243. `AddConstructorParameter`

244. `AddEnumValue`

245. `AddField`

246. `AddMemberToClass`

247. `AddModifier`

248. `AddProperty`

249. `AddSummaryComment`

250. `AddUsingDirective`

251. `AnalyzeControlFlow`

252. `AnalyzeDataFlow`

253. `ChangeAccessibility`

254. `ChangeSignature`

255. `ConvertAbstractToInterface`

256. `ConvertExpressionBody`

257. `ConvertMethodToIndexer`

258. `ConvertPropertyToMethods`

259. `ExtensionToStatic`

260. `ExtractConstant`

261. `ExtractInterface`

262. `ExtractMethod`

263. `ExtractSuperclass`

264. `FormatDocumentPreview`

265. `GenerateMapping`

266. `GetById`

267. `InlineField`

268. `InlineMethod`

269. `InlineParameter`

270. `InlineVariable`

271. `InsertMemberAfter`

272. `InsertMemberBefore`

273. `IntroduceField`

274. `IntroduceParameter`

275. `IntroduceParameterObject`

276. `IntroduceVariable`

277. `InvertAssignments`

278. `MakeMethodStatic`

279. `MoveAllTypesToFiles`

280. `MoveAllTypesToFilesInProject`

281. `MoveAllTypesToFilesInSolution`

282. `MoveTypeToFile`

283. `MoveTypeToOuterScope`

284. `OptimizeTaskWait`

285. `PullUpMember`

286. `ReduceBlockDepth`

287. `RemoveAttribute`

288. `RemoveBaseType`

289. `RemoveMember`

290. `RemoveModifier`

291. `RenameSymbol`

292. `ReplaceConstructorWithFactory`

293. `ReplaceMember`

294. `SafeDeleteSymbol`

295. `SortMembers`

296. `SyncInterfaceToImplementation`

297. `SyncTypeAndFilename`

298. `UpdateXmlDocsFromSignature`

299. `WrapInRegion`

300. `WrapInTryCatch`

301. `WrapInUsing`



**SentinelWorkspaceTools** (19 tools):

302. `ApplyProposedChanges`

303. `ApplyProposedDiff`

304. `ApplyStagedChanges`

305. `CreateProject`

306. `Diagnose`

307. `GetExternalChanges`

308. `GetFileDiagnostics`

309. `GetProjectDiagnostics`

310. `GetSolutionDiagnostics`

311. `GetStagedChanges`

312. `ListDependencies`

313. `LoadSolution`

314. `RetryFailedChanges`

315. `SafeDelete`

316. `SplitProjectByFolder`

317. `SyncTypeAndFilename`

318. `ValidateProposedChanges`

319. `ValidateProposedDiff`

320. `ValidateStagedChanges`




**Total tools documented:** 320


---

## Detailed Tool Documentation


### Engine-Based Tools


## AdvancedLogicEngine


**Type:** Engine  

**Tools:** 7


**Tools in this source:**


- ConvertForEachToForAsync

- ConvertForToForEachAsync

- ConvertIfToSwitchExpressionAsync

- ConvertIfToSwitchStatementAsync

- ConvertStaticToExtensionAsync

- ConvertWhileToForAsync

- ExtensionToStaticAsync


---


### ConvertForEachToForAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertForEachToForAsync(string filePath, int line, CancellationToken ct = default)
```


---


### ConvertForToForEachAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertForToForEachAsync(string filePath, int line, CancellationToken ct = default)
```


---


### ConvertIfToSwitchExpressionAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertIfToSwitchExpressionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### ConvertIfToSwitchStatementAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertIfToSwitchStatementAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### ConvertStaticToExtensionAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertStaticToExtensionAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### ConvertWhileToForAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertWhileToForAsync(string filePath, int line, CancellationToken ct = default)
```


---


### ExtensionToStaticAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ExtensionToStaticAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


## AdvancedRefactoringEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- OptimizeTaskWaitAsync

- ReplaceStringConcatWithInterpolationAsync


---


### OptimizeTaskWaitAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> OptimizeTaskWaitAsync(string filePath, CancellationToken cancellationToken = default)
```


---


### ReplaceStringConcatWithInterpolationAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ReplaceStringConcatWithInterpolationAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## AdvancedStructuralEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- ConvertAbstractClassToInterfaceAsync

- ReplaceConstructorWithFactoryAsync


---


### ConvertAbstractClassToInterfaceAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertAbstractClassToInterfaceAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


### ReplaceConstructorWithFactoryAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ReplaceConstructorWithFactoryAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


## AnalysisEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- FooAsync

- GenerateCallTreeAsync

- GenerateEqualityOverridesAsync


---


### FooAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public Task<T> FooAsync()
```


---


### GenerateCallTreeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateCallTreeAsync(string filePath, string methodName, int depth = 3, CancellationToken cancellationToken = default)
```


---


### GenerateEqualityOverridesAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateEqualityOverridesAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


## ApiAutomationEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- GenerateHttpClientForControllerAsync


---


### GenerateHttpClientForControllerAsync


**Purpose:**
/// Scans a Web API controller and generates a typed HttpClient for it. ///


**Signature:**

```csharp
public async Task<string> GenerateHttpClientForControllerAsync(string filePath, string controllerName, CancellationToken cancellationToken = default)
```


---


## ApiIntegrationEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- AddValidationToPocoAsync


---


### AddValidationToPocoAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddValidationToPocoAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


## ArchitecturalEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- ConvertToBackgroundServiceAsync


---


### ConvertToBackgroundServiceAsync


**Purpose:**
/// Converts a class into a .NET BackgroundService. ///


**Signature:**

```csharp
public async Task<string> ConvertToBackgroundServiceAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


## AsyncOptimizationEngine


**Type:** Engine  

**Tools:** 7


**Tools in this source:**


- AddCancellationTokenToMethodAsync

- AddConfigureAwaitFalseAsync

- ConvertToAsyncEnumerableAsync

- GenerateAsyncOverloadAsync

- OptimizeIndependentAwaitsAsync

- OptimizeToValueTaskAsync

- RemoveConfigureAwaitFalseAsync


---


### AddCancellationTokenToMethodAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddCancellationTokenToMethodAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### AddConfigureAwaitFalseAsync


**Purpose:**
/// Adds .ConfigureAwait(false) (or true) to all await expressions that don't already have it. ///


**Signature:**

```csharp
public async Task<string> AddConfigureAwaitFalseAsync(string filePath, bool libraryMode = true, CancellationToken cancellationToken = default)
```


---


### ConvertToAsyncEnumerableAsync


**Purpose:**
/// Converts a method returning Task&lt;List&lt;T&gt;&gt; or List&lt;T&gt; to IAsyncEnumerable&lt;T&gt;. /// Transforms results.Add(x) patterns to yield return x. Falls back to scaffold for complex bodies. ///


**Signature:**

```csharp
public async Task<string> ConvertToAsyncEnumerableAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### GenerateAsyncOverloadAsync


**Purpose:**
/// Creates an async version of a synchronous method. ///


**Signature:**

```csharp
public async Task<string> GenerateAsyncOverloadAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### OptimizeIndependentAwaitsAsync


**Purpose:**
/// Finds sequences of independent awaits and converts them to Task.WhenAll. ///


**Signature:**

```csharp
public async Task<string> OptimizeIndependentAwaitsAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### OptimizeToValueTaskAsync


**Purpose:**
/// Analyzes methods returning Task/Task and converts them to ValueTask/ValueTask if they frequently complete synchronously. /// Also updates interface signatures if the method implements an interface. ///


**Signature:**

```csharp
public async Task<string> OptimizeToValueTaskAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### RemoveConfigureAwaitFalseAsync


**Purpose:**
/// Removes all .ConfigureAwait(x) calls, leaving the bare awaited expression. ///


**Signature:**

```csharp
public async Task<string> RemoveConfigureAwaitFalseAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## CodeFlowEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- ReduceBlockDepthAsync


---


### ReduceBlockDepthAsync


**Purpose:**
/// Reduces block depth by finding if statements that encompass the whole method body and inverting them to return early. ///


**Signature:**

```csharp
public async Task<string> ReduceBlockDepthAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


## CodeGenerationEngine


**Type:** Engine  

**Tools:** 10


**Tools in this source:**


- ConvertPropertySafeAsync

- GenerateClassesFromJson

- GenerateConstructorAsync

- GenerateDecoratorClassAsync

- GenerateDefaultConfigJsonAsync

- GenerateFluentBuilderAsync

- GenerateRepositoryInterfaceAsync

- GenerateToStringAsync

- ImplementInterfaceAsync

- InterpolateStringAsync


---


### ConvertPropertySafeAsync


**Purpose:**
/// Converts a property between auto-property and full property with backing field. /// Unlike the built-in convert_property, this preserves initializers on ToFullProperty and /// correctly handles all modifiers (virtual, override, new). direction: "ToFullProperty" or "ToAutoProperty". /// contextSnippet: optional verbatim substring to disambiguate when multiple properties share a name. ///


**Signature:**

```csharp
public async Task<string> ConvertPropertySafeAsync(
        string filePath,
        string propertyName,
        string direction,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken ct = default)
```


---


### GenerateClassesFromJson


**Purpose:**
/// Generates C# classes from a JSON string. ///


**Signature:**

```csharp
public GenerationResult GenerateClassesFromJson(string json, string rootClassName, string @namespace)
```


---


### GenerateConstructorAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateConstructorAsync(string filePath, string className, CancellationToken ct = default)
```


---


### GenerateDecoratorClassAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DecoratorResult?> GenerateDecoratorClassAsync(
        string interfaceName,
        string decoratorPrefix = "Logging",
        string? projectName = null,
        CancellationToken ct = default)
```


---


### GenerateDefaultConfigJsonAsync


**Purpose:**
/// Scans a project for configuration usage (e.g. config["Key"]) and generates a JSON config file. ///


**Signature:**

```csharp
public async Task<string> GenerateDefaultConfigJsonAsync(string projectName, CancellationToken cancellationToken = default)
```


---


### GenerateFluentBuilderAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<FluentBuilderResult> GenerateFluentBuilderAsync(
        string filePath, string className, CancellationToken ct = default)
```


---


### GenerateRepositoryInterfaceAsync


**Purpose:**
/// Given a concrete repository class, generates: interface code, DI registration snippet, and Moq mock setup snippet. ///


**Signature:**

```csharp
public async Task<RepositoryInterfaceResult> GenerateRepositoryInterfaceAsync(
        string filePath, string className, CancellationToken ct = default)
```


---


### GenerateToStringAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<GenerateToStringResult> GenerateToStringAsync(
        string filePath,
        string className,
        string[]? excludeProperties = null,
        CancellationToken ct = default)
```


---


### ImplementInterfaceAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ImplementInterfaceAsync(string filePath, string className, string interfaceName, CancellationToken ct = default)
```


---


### InterpolateStringAsync


**Purpose:**
/// Converts a string.Format(...) call to an interpolated string ($"..."). /// Unlike the built-in convert_to_interpolated_string, this resolves const string format arguments /// via the semantic model so it works even when the format string is not a literal. /// contextSnippet: verbatim substring identifying the string.Format call to convert. ///


**Signature:**

```csharp
public async Task<string> InterpolateStringAsync(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken ct = default)
```


---


## CodeHealingEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- AddRetryPolicyAsync

- FixThreadSleepAsync


---


### AddRetryPolicyAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddRetryPolicyAsync(string f, int sl, int el, int rc)
```


---


### FixThreadSleepAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> FixThreadSleepAsync(string filePath, CancellationToken ct = default)
```


---


## CodeSmellAndStyleEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- UseSwitchExpressionAsync


---


### UseSwitchExpressionAsync


**Purpose:**
/// Implements IDE0066: Use switch expression. ///


**Signature:**

```csharp
public async Task<string> UseSwitchExpressionAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## CodeStyleEngine


**Type:** Engine  

**Tools:** 7


**Tools in this source:**


- ConvertPropertyToMethodsAsync

- FixDangerousLockAsync

- SimplifyAllNamesAsync

- SimplifyVerbosityAsync

- UseCollectionExpressionsAsync

- UseIndexFromEndAsync

- UseTimeProviderAsync


---


### ConvertPropertyToMethodsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertPropertyToMethodsAsync(string filePath, string propertyName, CancellationToken ct = default)
```


---


### FixDangerousLockAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> FixDangerousLockAsync(string filePath, CancellationToken ct = default)
```


---


### SimplifyAllNamesAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SimplifyAllNamesAsync(string filePath, CancellationToken ct = default)
```


---


### SimplifyVerbosityAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SimplifyVerbosityAsync(string filePath, CancellationToken ct = default)
```


---


### UseCollectionExpressionsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseCollectionExpressionsAsync(string filePath, CancellationToken ct = default)
```


---


### UseIndexFromEndAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseIndexFromEndAsync(string filePath, CancellationToken ct = default)
```


---


### UseTimeProviderAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseTimeProviderAsync(string filePath, CancellationToken ct = default)
```


---


## ControlFlowEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- AnalyzeMethodControlFlowAsync

- AnalyzeMethodDataFlowAsync

- AnalyzePathCoverageAsync


---


### AnalyzeMethodControlFlowAsync


**Purpose:**
/// Analyzes control flow for an entire method body using Roslyn's semantic analysis. /// Takes the method name (not raw line ranges) — avoids the "include method signature" trap. /// If multiple overloads exist, provide disambiguateLine (any line inside the desired overload). ///


**Signature:**

```csharp
public async Task<ControlFlowAnalysisResult> AnalyzeMethodControlFlowAsync(
        string filePath,
        string methodName,
        int? disambiguateLine = null,
        CancellationToken ct = default)
```


---


### AnalyzeMethodDataFlowAsync


**Purpose:**
/// Analyzes data flow for an entire method body using Roslyn's semantic analysis. /// Takes the method name (not raw line ranges) — avoids the "include method signature" trap. /// If multiple overloads exist, provide disambiguateLine (any line inside the desired overload). ///


**Signature:**

```csharp
public async Task<DataFlowAnalysisResult> AnalyzeMethodDataFlowAsync(
        string filePath,
        string methodName,
        int? disambiguateLine = null,
        CancellationToken ct = default)
```


---


### AnalyzePathCoverageAsync


**Purpose:**
/// Analyzes a method and returns a list of all logic paths that need test coverage. ///


**Signature:**

```csharp
public async Task<PathCoverageReport> AnalyzePathCoverageAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


## DependencyEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- GetProjectDependenciesAsync


---


### GetProjectDependenciesAsync


**Purpose:**
/// Returns all project and NuGet dependencies for a specific project. ///


**Signature:**

```csharp
public async Task<ProjectDependencyReport> GetProjectDependenciesAsync(string projectName)
```


---


## DependencyInjectionEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- AddDependencyAsync


---


### AddDependencyAsync


**Purpose:**
/// Injects a new dependency into a class constructor and adds the corresponding private field. ///


**Signature:**

```csharp
public async Task<string> AddDependencyAsync(string filePath, string className, string dependencyType, string dependencyName, CancellationToken cancellationToken = default)
```


---


## DiagnosticEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- GetFileDiagnosticsAsync

- GetProjectDiagnosticsAsync

- GetSolutionDiagnosticsAsync


---


### GetFileDiagnosticsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticSummary> GetFileDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
```


---


### GetProjectDiagnosticsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticSummary> GetProjectDiagnosticsAsync(string projectName, CancellationToken cancellationToken = default)
```


---


### GetSolutionDiagnosticsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticSummary> GetSolutionDiagnosticsAsync(CancellationToken cancellationToken = default)
```


---


## DiffEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- ApplyDiff


---


### ApplyDiff


**Purpose:**
/// Applies a standard Unified Diff to a SourceText object and returns the updated text. /// Supports multiple hunks and validates context lines. ///


**Signature:**

```csharp
public SourceText ApplyDiff(SourceText sourceText, string unifiedDiff)
```


---


## DiscoveryEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- FindBestInsertionPointAsync

- PreviewRenameImpactAsync


---


### FindBestInsertionPointAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<BestInsertionResult> FindBestInsertionPointAsync(
        string filePath, string containerName, string memberKind, CancellationToken ct = default)
```


---


### PreviewRenameImpactAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<RenameImpactPreview> PreviewRenameImpactAsync(
        string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


## DocumentationEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- DocumentPocoFieldsAsync

- GenerateXmlDocumentationStubsAsync


---


### DocumentPocoFieldsAsync


**Purpose:**
"); sb.AppendLine($"/// TODO: Add description for {newMethod.Identifier.Text}."); sb.AppendLine("///


**Signature:**

```csharp
public async Task<string> DocumentPocoFieldsAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


### GenerateXmlDocumentationStubsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateXmlDocumentationStubsAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## GranularRefactoringEngine


**Type:** Engine  

**Tools:** 9


**Tools in this source:**


- ConvertMethodToIndexerAsync

- InlineFieldAsync

- InlineParameterAsync

- IntroduceFieldAsync

- IntroduceParameterAsync

- IntroduceParameterObjectAsync

- IntroduceVariableAsync

- MoveTypeToOuterScopeAsync

- RunMicroRefactoringAsync


---


### ConvertMethodToIndexerAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertMethodToIndexerAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### InlineFieldAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InlineFieldAsync(string filePath, string fieldName, CancellationToken cancellationToken = default)
```


---


### InlineParameterAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InlineParameterAsync(string filePath, string methodName, string parameterName, CancellationToken cancellationToken = default)
```


---


### IntroduceFieldAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceFieldAsync(string filePath, string contextSnippet, string newFieldName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
```


---


### IntroduceParameterAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceParameterAsync(string filePath, string contextSnippet, string newParamName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
```


---


### IntroduceParameterObjectAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceParameterObjectAsync(
        string filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null,
        CancellationToken cancellationToken = default)
```


---


### IntroduceVariableAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceVariableAsync(string filePath, string contextSnippet, string newVariableName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
```


---


### MoveTypeToOuterScopeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MoveTypeToOuterScopeAsync(string filePath, string nestedTypeName, CancellationToken cancellationToken = default)
```


---


### RunMicroRefactoringAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RunMicroRefactoringAsync(string filePath, string refactoringId, int line, CancellationToken cancellationToken = default)
```


---


## HealthOrchestrationEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- GenerateComprehensiveHealthReportAsync


---


### GenerateComprehensiveHealthReportAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ComprehensiveHealthReport> GenerateComprehensiveHealthReportAsync(
        List<HealthEngineType>? engines = null,
        string? projectName = null,
        string? filePath = null,
        int offset = 0,
        int limit = 10,
        int timeoutSeconds = 25,
        CancellationToken cancellationToken = default)
```


---


## IDEStyleEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- SimplifyMemberAccessAsync

- UseNullPropagationAsync

- UseObjectInitializersAsync


---


### SimplifyMemberAccessAsync


**Purpose:**
/// Simplifies member access by removing unnecessary 'this.' or base qualifiers. ///


**Signature:**

```csharp
public async Task<string> SimplifyMemberAccessAsync(string filePath, CancellationToken cancellationToken = default)
```


---


### UseNullPropagationAsync


**Purpose:**
/// Upgrades traditional null checks to null-propagation (?. ) usage. ///


**Signature:**

```csharp
public async Task<string> UseNullPropagationAsync(string filePath, CancellationToken cancellationToken = default)
```


---


### UseObjectInitializersAsync


**Purpose:**
/// Converts standard assignments to object initializers. ///


**Signature:**

```csharp
public async Task<string> UseObjectInitializersAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## ImmutabilityEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- MakeClassImmutableAsync


---


### MakeClassImmutableAsync


**Purpose:**
/// Converts a class to be immutable by making fields readonly and properties init-only. ///


**Signature:**

```csharp
public async Task<string> MakeClassImmutableAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


## InstrumentationEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- AddStopwatchDiagnosticsAsync

- AddTryCatchToClassAsync

- AddTryCatchToMethodAsync


---


### AddStopwatchDiagnosticsAsync


**Purpose:**
/// Adds Stopwatch diagnostics (prefix start, postfix stop and log) to a method. ///


**Signature:**

```csharp
public async Task<string> AddStopwatchDiagnosticsAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### AddTryCatchToClassAsync


**Purpose:**
/// Wraps all public methods in a class in try/catch blocks. ///


**Signature:**

```csharp
public async Task<string> AddTryCatchToClassAsync(string filePath, string className, string exceptionType = "Exception", CancellationToken cancellationToken = default)
```


---


### AddTryCatchToMethodAsync


**Purpose:**
/// Wraps a method's body in a try/catch/finally block. ///


**Signature:**

```csharp
public async Task<string> AddTryCatchToMethodAsync(string filePath, string methodName, string exceptionType = "Exception", bool addFinally = false, CancellationToken cancellationToken = default)
```


---


## InventoryEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- GetCodeInventoryAsync


---


### GetCodeInventoryAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<CodeInventoryReport> GetCodeInventoryAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## LogicOptimizationEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- AddGuardClausesAsync

- SimplifyBooleanExpressionsAsync


---


### AddGuardClausesAsync


**Purpose:**
/// Adds ArgumentNullException.ThrowIfNull checks to all reference type parameters in a method. ///


**Signature:**

```csharp
public async Task<string> AddGuardClausesAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### SimplifyBooleanExpressionsAsync


**Purpose:**
/// Simplifies redundant logic like 'if (x == true)' to 'if (x)'. ///


**Signature:**

```csharp
public async Task<string> SimplifyBooleanExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## MappingEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- GenerateMappingAsync

- InvertAssignmentsAsync


---


### GenerateMappingAsync


**Purpose:**
/// Generates a mapping method between two types based on property names. ///


**Signature:**

```csharp
public async Task<string> GenerateMappingAsync(string filePath, string fromType, string toType, CancellationToken cancellationToken = default)
```


---


### InvertAssignmentsAsync


**Purpose:**
/// Inverts the direction of all assignments in a selected block of code. ///


**Signature:**

```csharp
public async Task<string> InvertAssignmentsAsync(string filePath, int startLine, int endLine, CancellationToken cancellationToken = default)
```


---


## MetricsEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- GetSolutionMetricsAsync


---


### GetSolutionMetricsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<SolutionMetrics> GetSolutionMetricsAsync(string? projectName = null, CancellationToken cancellationToken = default)
```


---


## ModernLoggingEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- ConvertToSourceGeneratedLoggingAsync


---


### ConvertToSourceGeneratedLoggingAsync


**Purpose:**
/// Converts a standard logger call (e.g. _logger.LogInformation("Msg {Param}", p)) into a source-generated [LoggerMessage] method. ///


**Signature:**

```csharp
public async Task<string> ConvertToSourceGeneratedLoggingAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


## ModernizationEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- ClassToRecordAsync

- ConvertMethodToExpressionBodyAsync

- RecordToClassAsync


---


### ClassToRecordAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ClassToRecordAsync(string filePath, string className, CancellationToken cancellationToken = default)
```


---


### ConvertMethodToExpressionBodyAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertMethodToExpressionBodyAsync(string filePath, string methodName, CancellationToken ct = default)
```


---


### RecordToClassAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RecordToClassAsync(string filePath, string recordName, CancellationToken cancellationToken = default)
```


---


## ModernizationUpgradeEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- UpgradePatternMatchingAsync

- UseSpanForParsingAsync

- UseThrowExpressionsAsync


---


### UpgradePatternMatchingAsync


**Purpose:**
/// Upgrades code to use modern pattern matching (is Type t) instead of casts. ///


**Signature:**

```csharp
public async Task<string> UpgradePatternMatchingAsync(string filePath, CancellationToken cancellationToken = default)
```


---


### UseSpanForParsingAsync


**Purpose:**
/// Upgrades legacy string parsing to use Span for zero-allocation performance. ///


**Signature:**

```csharp
public async Task<string> UseSpanForParsingAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### UseThrowExpressionsAsync


**Purpose:**
/// Converts traditional throws to throw expressions (IDE0016). ///


**Signature:**

```csharp
public async Task<string> UseThrowExpressionsAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## MsToolAugmentEngine


**Type:** Engine  

**Tools:** 10


**Tools in this source:**


- AnalyzeForeachForLinqConversionAsync

- AnalyzeSwitchForPatternConversionAsync

- ConvertStringFormatToInterpolatedSmartAsync

- ConvertSwitchToPatternSafeAsync

- EncapsulateFieldSafeAsync

- ExtractConstantSafeAsync

- FormatDocumentSafeAsync

- GetWorkspaceHealthAsync

- PreviewAddMissingUsingsAsync

- SortAndDeduplicateUsingsAsync


---


### AnalyzeForeachForLinqConversionAsync


**Purpose:**
/// Pre-flight safety analysis for the standard convert_foreach_linq tool. /// Detects the case where the collection is modified before the foreach — which the /// standard tool silently destroys by re-initializing the collection variable. ///


**Signature:**

```csharp
public async Task<ForeachLinqAnalysis> AnalyzeForeachForLinqConversionAsync(
        string filePath, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken ct = default)
```


---


### AnalyzeSwitchForPatternConversionAsync


**Purpose:**
/// Analyzes a switch statement to determine whether it is safe for pattern-matching /// conversion. Use this before calling the standard convert_to_pattern_matching /// tool to avoid silent data loss. ///


**Signature:**

```csharp
public async Task<SwitchConversionAnalysis> AnalyzeSwitchForPatternConversionAsync(
        string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### ConvertStringFormatToInterpolatedSmartAsync


**Purpose:**
/// Converts a string.Format() call to an interpolated string. Unlike the /// standard tool, this works when the format argument is a named constant /// (e.g., string.Format(MyConst, arg1, arg2)). ///


**Signature:**

```csharp
public async Task<MsAugmentResult> ConvertStringFormatToInterpolatedSmartAsync(
        string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### ConvertSwitchToPatternSafeAsync


**Purpose:**
/// Converts a switch statement to a switch expression. Unlike the standard /// convert_to_pattern_matching tool, this version rejects switch statements /// where cases assign to multiple variables — preventing silent data loss. ///


**Signature:**

```csharp
public async Task<MsAugmentResult> ConvertSwitchToPatternSafeAsync(
        string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### EncapsulateFieldSafeAsync


**Purpose:**
/// Encapsulates a public field as a private backing field + public property. /// The backing field is always named _camelCase to avoid the self-referential /// property bug in the standard encapsulate_field tool. ///


**Signature:**

```csharp
public async Task<MsAugmentResult> EncapsulateFieldSafeAsync(
        string filePath, string fieldName, string? overridePropertyName = null,
        CancellationToken ct = default)
```


---


### ExtractConstantSafeAsync


**Purpose:**
/// Extracts a literal expression to a named constant, using contextSnippet /// to locate the literal instead of fragile line/column coordinates. /// Fixes the standard extract_constant tool's cryptic "Column 99 is beyond /// end of line" error. Replaces ALL identical literals in the file. ///


**Signature:**

```csharp
public async Task<MsAugmentResult> ExtractConstantSafeAsync(
        string filePath, string contextSnippet, string constantName,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken ct = default)
```


---


### FormatDocumentSafeAsync


**Purpose:**
/// Formats a C# file using Roslyn's built-in formatter, with true preview support. /// Unlike the standard format_document tool, preview=true (the default) /// returns the formatted content WITHOUT modifying the file on disk. ///


**Signature:**

```csharp
public async Task<MsAugmentResult> FormatDocumentSafeAsync(
        string filePath, bool preview = true, CancellationToken ct = default)
```


---


### GetWorkspaceHealthAsync


**Purpose:**
/// Returns a targeted workspace health report based on actual solution state. /// Fixes the false-negative in the standard diagnose tool, which reports /// healthy: false even when all projects load successfully. ///


**Signature:**

```csharp
public Task<WorkspaceHealthReport> GetWorkspaceHealthAsync(CancellationToken ct = default)
```


---


### PreviewAddMissingUsingsAsync


**Purpose:**
/// Computes which using directives would be added without modifying the file. /// Fixes the standard add_missing_usings tool's bug where preview:true /// is silently ignored and the file is modified on disk. ///


**Signature:**

```csharp
public async Task<AddUsingsPreview> PreviewAddMissingUsingsAsync(
        string filePath, CancellationToken ct = default)
```


---


### SortAndDeduplicateUsingsAsync


**Purpose:**
/// Sorts using directives alphabetically (System.* first) AND removes /// duplicates in a single operation. Fixes the gap between sort_usings /// (no dedup) and remove_unused_usings (won't remove a duplicate that /// is technically "used"). ///


**Signature:**

```csharp
public async Task<UsingsCleanupResult> SortAndDeduplicateUsingsAsync(
        string filePath, CancellationToken ct = default)
```


---


## ProjectStructureEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- FixMismatchedNamespacesAsync

- MoveFileToNamespaceFolderAsync


---


### FixMismatchedNamespacesAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> FixMismatchedNamespacesAsync(string filePath, CancellationToken cancellationToken = default)
```


---


### MoveFileToNamespaceFolderAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MoveFileToNamespaceFolderAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## RefactoringEngine


**Type:** Engine  

**Tools:** 34


**Tools in this source:**


- AddAttributeAsync

- AddBaseTypeAsync

- AddConstructorParameterAsync

- AddEnumValueAsync

- AddFieldAsync

- AddMemberAsync

- AddModifierAsync

- AddPropertyAsync

- AddRemoveParamsAsync

- AddSummaryCommentAsync

- AddUsingDirectiveAsync

- AnalyzeControlFlowAsync

- AnalyzeDataFlowAsync

- ChangeAccessibilityAsync

- ConvertExpressionBodyAsync

- ConvertIndexerToMethodAsync

- ConvertToPrimaryConstructorAsync

- ExtractConstantAsync

- ExtractMethodAsync

- FormatDocumentAsync

- FormatDocumentPreviewAsync

- InsertMemberAfterAsync

- InsertMemberBeforeAsync

- RemoveAttributeAsync

- RemoveBaseTypeAsync

- RemoveMemberAsync

- RemoveModifierAsync

- RenameSymbolAsync

- ReplaceMemberAsync

- SortMembersAsync

- SyncInterfaceToImplementationAsync

- UpdateXmlDocsFromSignatureAsync

- WrapInRegionAsync

- WrapInTryCatchAsync


---


### AddAttributeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddAttributeAsync(string filePath, string targetName, string attributeSource, CancellationToken ct = default)
```


---


### AddBaseTypeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddBaseTypeAsync(string filePath, string typeName, string baseTypeName, CancellationToken ct = default)
```


---


### AddConstructorParameterAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddConstructorParameterAsync(string filePath, string className, string paramName, string paramType, string? fieldName = null, CancellationToken ct = default)
```


---


### AddEnumValueAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddEnumValueAsync(string filePath, string enumName, string valueName, int? explicitValue = null, CancellationToken ct = default)
```


---


### AddFieldAsync


**Purpose:**
\n/// {summaryText}\n///


**Signature:**

```csharp
public async Task<string> AddFieldAsync(string filePath, string containerName, string fieldName, string fieldType, string accessibility = "private", bool isReadonly = false, bool isStatic = false, string? initializer = null, CancellationToken ct = default)
```


---


### AddMemberAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddMemberAsync(string filePath, string containerName, string newMemberSource, CancellationToken ct = default)
```


---


### AddModifierAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddModifierAsync(string filePath, string targetName, string modifier, CancellationToken ct = default)
```


---


### AddPropertyAsync


**Purpose:**
\n/// {summaryText}\n///


**Signature:**

```csharp
public async Task<string> AddPropertyAsync(string filePath, string containerName, string propertyName, string propertyType, string accessibility = "public", bool hasSetter = true, bool isInit = false, CancellationToken ct = default)
```


---


### AddRemoveParamsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddRemoveParamsAsync(string filePath, string methodName, CancellationToken ct = default)
```


---


### AddSummaryCommentAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddSummaryCommentAsync(string filePath, string targetName, string summaryText, CancellationToken ct = default)
```


---


### AddUsingDirectiveAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddUsingDirectiveAsync(string filePath, string namespaceName, CancellationToken ct = default)
```


---


### AnalyzeControlFlowAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ControlFlowSummary> AnalyzeControlFlowAsync(string filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### AnalyzeDataFlowAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DataFlowSummary> AnalyzeDataFlowAsync(string filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### ChangeAccessibilityAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ChangeAccessibilityAsync(string filePath, string targetName, string accessibility, CancellationToken ct = default)
```


---


### ConvertExpressionBodyAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertExpressionBodyAsync(string filePath, string memberName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### ConvertIndexerToMethodAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertIndexerToMethodAsync(string filePath, CancellationToken ct = default)
```


---


### ConvertToPrimaryConstructorAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertToPrimaryConstructorAsync(string filePath, string className, CancellationToken ct = default)
```


---


### ExtractConstantAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ExtractConstantAsync(string filePath, string contextSnippet, string constantName, string visibility = "private", string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### ExtractMethodAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ExtractMethodResult> ExtractMethodAsync(
        string filePath, int startLine, string startLineText, int endLine, string endLineText,
        string newMethodName, CancellationToken ct = default)
```


---


### FormatDocumentAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> FormatDocumentAsync(string filePath, CancellationToken ct = default)
```


---


### FormatDocumentPreviewAsync


**Purpose:**
/// Returns a preview of what FormatDocument would change without applying changes. /// Shows changed line ranges with ±3 lines of context (like a unified diff). /// Returns Changed=false and an empty hunks list if the file is already formatted correctly. ///


**Signature:**

```csharp
public async Task<FormatPreviewResult> FormatDocumentPreviewAsync(string filePath, CancellationToken ct = default)
```


---


### InsertMemberAfterAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InsertMemberAfterAsync(string filePath, string containerName, string afterMemberName, string newMemberSource, CancellationToken ct = default)
```


---


### InsertMemberBeforeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InsertMemberBeforeAsync(string filePath, string containerName, string beforeMemberName, string newMemberSource, CancellationToken ct = default)
```


---


### RemoveAttributeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RemoveAttributeAsync(string filePath, string targetName, string attributeName, CancellationToken ct = default)
```


---


### RemoveBaseTypeAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RemoveBaseTypeAsync(string filePath, string typeName, string baseTypeName, CancellationToken ct = default)
```


---


### RemoveMemberAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RemoveMemberAsync(string filePath, string memberName, CancellationToken ct = default)
```


---


### RemoveModifierAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RemoveModifierAsync(string filePath, string targetName, string modifier, CancellationToken ct = default)
```


---


### RenameSymbolAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<RenameSymbolResult> RenameSymbolAsync(string filePath, string symbolName, string contextSnippet, string newName, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


### ReplaceMemberAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ReplaceMemberAsync(string filePath, string memberName, string newSource, CancellationToken ct = default)
```


---


### SortMembersAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SortMembersAsync(string filePath, string containerName, CancellationToken ct = default)
```


---


### SyncInterfaceToImplementationAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SyncInterfaceToImplementationAsync(string filePath, string className, string interfaceName, CancellationToken ct = default)
```


---


### UpdateXmlDocsFromSignatureAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpdateXmlDocsFromSignatureAsync(string filePath, string methodName, CancellationToken ct = default)
```


---


### WrapInRegionAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> WrapInRegionAsync(string filePath, int startLine, int endLine, string regionName, CancellationToken ct = default)
```


---


### WrapInTryCatchAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> WrapInTryCatchAsync(string filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken ct = default)
```


---


## RefinementEngine


**Type:** Engine  

**Tools:** 1


**Tools in this source:**


- InlineMethodAsync


---


### InlineMethodAsync


**Purpose:**
/// Inlines a simple single-statement method by replacing all its call sites with the statement's expression. ///


**Signature:**

```csharp
public async Task<string> InlineMethodAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


## SolutionManagementEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- CreateProjectAsync

- SplitProjectByFolderAsync


---


### CreateProjectAsync


**Purpose:**
/// Creates a new project within the solution. ///


**Signature:**

```csharp
public async Task<string> CreateProjectAsync(string projectName, string projectType = "console", CancellationToken cancellationToken = default)
```


---


### SplitProjectByFolderAsync


**Purpose:**
/// Splits a project by moving a folder's contents into a new project and updating references. ///


**Signature:**

```csharp
public async Task<string> SplitProjectByFolderAsync(string sourceProjectName, string folderName, string targetProjectName, CancellationToken cancellationToken = default)
```


---


## StandardRefactoringEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- ConvertMethodToPropertyAsync

- InvertBooleanAsync

- MakeMethodStaticAsync


---


### ConvertMethodToPropertyAsync


**Purpose:**
/// Converts a method with no parameters to a property. ///


**Signature:**

```csharp
public async Task<string> ConvertMethodToPropertyAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### InvertBooleanAsync


**Purpose:**
/// Inverts a boolean variable or parameter name and its usages. ///


**Signature:**

```csharp
public async Task<string> InvertBooleanAsync(string filePath, string boolName, CancellationToken cancellationToken = default)
```


---


### MakeMethodStaticAsync


**Purpose:**
/// Makes a method static if it doesn't access any instance members. ///


**Signature:**

```csharp
public async Task<string> MakeMethodStaticAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


## StructuralRefinementEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- SafeDeleteSymbolAsync

- SyncTypeAndFilenameAsync


---


### SafeDeleteSymbolAsync


**Purpose:**
/// Safe deletes a symbol only if it has no usages in the entire solution. ///


**Signature:**

```csharp
public async Task<string> SafeDeleteSymbolAsync(string filePath, int line, int column, CancellationToken cancellationToken = default)
```


---


### SyncTypeAndFilenameAsync


**Purpose:**
/// Synchronizes the filename to match the primary type declared in the file. /// Uses staging mechanism (returns change ID) instead of direct file writes. ///


**Signature:**

```csharp
public async Task<string> SyncTypeAndFilenameAsync(string filePath, CancellationToken cancellationToken = default)
```


---


## SymbolNavigationEngine


**Type:** Engine  

**Tools:** 3


**Tools in this source:**


- GetCallGraphAsync

- GetReverseCallGraphAsync

- GetSymbolInfoAsync


---


### GetCallGraphAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<CallGraphNode?> GetCallGraphAsync(
        string filePath,
        string methodName,
        int maxDepth = 3,
        CancellationToken ct = default)
```


---


### GetReverseCallGraphAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ReverseCallGraphNode?> GetReverseCallGraphAsync(
        string filePath,
        string methodName,
        int maxDepth = 3,
        CancellationToken ct = default)
```


---


### GetSymbolInfoAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<SymbolHoverInfo?> GetSymbolInfoAsync(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


## SyntaxUpgradeEngine


**Type:** Engine  

**Tools:** 10


**Tools in this source:**


- AddBracesAsync

- CleanupImplicitSpansAsync

- ConvertSwitchExpressionToStatementAsync

- ConvertSwitchToExpressionAsync

- UpgradePatternMatchingAsync

- UpgradeToModernGuardsAsync

- UpgradeToPrimaryConstructorAsync

- UseExceptionExpressionsAsync

- UseFieldBackedPropertiesAsync

- UseNameofExpressionAsync


---


### AddBracesAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddBracesAsync(string filePath, CancellationToken ct = default)
```


---


### CleanupImplicitSpansAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> CleanupImplicitSpansAsync(string filePath, CancellationToken ct = default)
```


---


### ConvertSwitchExpressionToStatementAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertSwitchExpressionToStatementAsync(string filePath, CancellationToken ct = default)
```


---


### ConvertSwitchToExpressionAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertSwitchToExpressionAsync(string filePath, string methodName, CancellationToken ct = default)
```


---


### UpgradePatternMatchingAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradePatternMatchingAsync(string filePath, CancellationToken ct = default)
```


---


### UpgradeToModernGuardsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradeToModernGuardsAsync(string filePath, CancellationToken ct = default)
```


---


### UpgradeToPrimaryConstructorAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradeToPrimaryConstructorAsync(string filePath, string className, CancellationToken ct = default)
```


---


### UseExceptionExpressionsAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseExceptionExpressionsAsync(string filePath, string methodName, CancellationToken ct = default)
```


---


### UseFieldBackedPropertiesAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseFieldBackedPropertiesAsync(string filePath, CancellationToken ct = default)
```


---


### UseNameofExpressionAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseNameofExpressionAsync(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
```


---


## TestingEngine


**Type:** Engine  

**Tools:** 4


**Tools in this source:**


- AddBenchmarkStubAsync

- CalculateComplexityAsync

- GenerateTestScaffoldAsync

- GenerateTestSkeletonAsync


---


### AddBenchmarkStubAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddBenchmarkStubAsync(string filePath, string className, string methodName, CancellationToken cancellationToken = default)
```


---


### CalculateComplexityAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<TestComplexityReport> CalculateComplexityAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### GenerateTestScaffoldAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<TestScaffoldResult> GenerateTestScaffoldAsync(string filePath, string className, CancellationToken ct = default)
```


---


### GenerateTestSkeletonAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<TestSkeletonReport> GenerateTestSkeletonAsync(string filePath, string className, string framework = "NUnit", CancellationToken cancellationToken = default)
```


---


## ThreadSafetyEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- ConvertLockToSemaphoreSlimAsync

- MakeMethodThreadSafeAsync


---


### ConvertLockToSemaphoreSlimAsync


**Purpose:**
/// Converts lock statements inside a method and ALL other methods to async-safe SemaphoreSlim pattern. /// Adds a SemaphoreSlim field and replaces all lock statements with await _semaphore.WaitAsync() + try/finally. ///


**Signature:**

```csharp
public async Task<string> ConvertLockToSemaphoreSlimAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
```


---


### MakeMethodThreadSafeAsync


**Purpose:**
/// Adds a private lock object and wraps a method's body in a lock statement. ///


**Signature:**

```csharp
public async Task<string> MakeMethodThreadSafeAsync(string filePath, string methodName, string lockFieldName = "_lock", CancellationToken cancellationToken = default)
```


---


## ValidationEngine


**Type:** Engine  

**Tools:** 2


**Tools in this source:**


- ValidateChangesAsync

- ValidateDiffAsync


---


### ValidateChangesAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticReport> ValidateChangesAsync(Dictionary<string, string> fileChanges, CancellationToken cancellationToken = default)
```


---


### ValidateDiffAsync


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticReport> ValidateDiffAsync(string filePath, string unifiedDiff, CancellationToken cancellationToken = default)
```


---



---

### Sentinel Tool Classes


## SentinelAugmentTools


**Type:** Tool Class  

**Tools:** 10


**Tools in this source:**


- AnalyzeForeachForLinqConversion

- AnalyzeSwitchForPatternConversion

- ConvertStringFormatToInterpolatedSmart

- ConvertSwitchToPatternSafe

- EncapsulateFieldSafe

- ExtractConstantSafe

- FormatDocumentSafe

- GetWorkspaceHealth

- PreviewAddMissingUsings

- SortAndDeduplicateUsings


---


### AnalyzeForeachForLinqConversion


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ForeachLinqAnalysis> AnalyzeForeachForLinqConversion(
        string filePath, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
```


---


### AnalyzeSwitchForPatternConversion


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<SwitchConversionAnalysis> AnalyzeSwitchForPatternConversion(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null)
```


---


### ConvertStringFormatToInterpolatedSmart


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<MsAugmentResult> ConvertStringFormatToInterpolatedSmart(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null)
```


---


### ConvertSwitchToPatternSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<MsAugmentResult> ConvertSwitchToPatternSafe(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null)
```


---


### EncapsulateFieldSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<MsAugmentResult> EncapsulateFieldSafe(
        string filePath,
        string fieldName,
        string? overridePropertyName = null)
```


---


### ExtractConstantSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<MsAugmentResult> ExtractConstantSafe(
        string filePath, string contextSnippet, string constantName,
        string? lineBefore = null, string? lineAfter = null)
```


---


### FormatDocumentSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<MsAugmentResult> FormatDocumentSafe(string filePath, bool preview = true)
```


---


### GetWorkspaceHealth


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<WorkspaceHealthReport> GetWorkspaceHealth()
```


---


### PreviewAddMissingUsings


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<AddUsingsPreview> PreviewAddMissingUsings(string filePath)
```


---


### SortAndDeduplicateUsings


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<UsingsCleanupResult> SortAndDeduplicateUsings(string filePath)
```


---


## SentinelGenerationTools


**Type:** Tool Class  

**Tools:** 13


**Tools in this source:**


- AddValidationToPoco

- ConvertPropertySafe

- GenerateAsyncOverload

- GenerateClassesFromJson

- GenerateConstructor

- GenerateDecoratorClass

- GenerateDefaultConfigJson

- GenerateFluentBuilder

- GenerateHttpClient

- GenerateRepositoryInterface

- GenerateToString

- ImplementInterfaceSafe

- InterpolateStringSafe


---


### AddValidationToPoco


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddValidationToPoco(string filePath, string className)
```


---


### ConvertPropertySafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertPropertySafe(
        string filePath, string propertyName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
```


---


### GenerateAsyncOverload


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateAsyncOverload(string filePath, string methodName)
```


---


### GenerateClassesFromJson


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public GenerationResult GenerateClassesFromJson(string json, string rootClassName, string @namespace)
```


---


### GenerateConstructor


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateConstructor(string filePath, string className)
```


---


### GenerateDecoratorClass


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DecoratorResult?> GenerateDecoratorClass(string interfaceName, string decoratorPrefix = "Logging", string? projectName = null)
```


---


### GenerateDefaultConfigJson


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateDefaultConfigJson(string projectName)
```


---


### GenerateFluentBuilder


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<FluentBuilderResult> GenerateFluentBuilder(string filePath, string className)
```


---


### GenerateHttpClient


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateHttpClient(string filePath, string controllerName)
```


---


### GenerateRepositoryInterface


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<RepositoryInterfaceResult> GenerateRepositoryInterface(string filePath, string className)
```


---


### GenerateToString


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<CodeGenerationEngine.GenerateToStringResult> GenerateToString(
        string filePath,
        string className,
        string[]? excludeProperties = null)
```


---


### ImplementInterfaceSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ImplementInterfaceSafe(string filePath, string className, string interfaceName)
```


---


### InterpolateStringSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InterpolateStringSafe(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
```


---


## SentinelIntelligenceTools


**Type:** Tool Class  

**Tools:** 16


**Tools in this source:**


- ConvertToBackgroundService

- DocumentPocoFields

- FindBestInsertionPoint

- FixMismatchedNamespaces

- GenerateCallTree

- GenerateEqualityOverrides

- GetBlastRadius

- GetById

- GetCallGraph

- GetCodeInventory

- GetComprehensiveHealthReport

- GetReverseCallGraph

- GetSolutionMetrics

- GetSymbolInfo

- MoveFileToNamespaceFolder

- PreviewRenameImpact


---


### ConvertToBackgroundService


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertToBackgroundService(string filePath, string className)
```


---


### DocumentPocoFields


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> DocumentPocoFields(string filePath, string className)
```


---


### FindBestInsertionPoint


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<BestInsertionResult> FindBestInsertionPoint(string filePath, string containerName, string memberKind)
```


---


### FixMismatchedNamespaces


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> FixMismatchedNamespaces(string filePath)
```


---


### GenerateCallTree


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateCallTree(string filePath, string methodName, int depth = 3)
```


---


### GenerateEqualityOverrides


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateEqualityOverrides(string filePath, string className)
```


---


### GetBlastRadius


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ImpactReport> GetBlastRadius(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
```


---


### GetById


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<T> GetById('). Provide lineBefore and/or lineAfter when the snippet could match multiple locations. Returns all call sites and affected projects.")]
    public async Task<ImpactReport> GetBlastRadius(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
```


---


### GetCallGraph


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<CallGraphNode?> GetCallGraph(
        string filePath, string methodName, int maxDepth = 3)
```


---


### GetCodeInventory


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<CodeInventoryReport> GetCodeInventory(string filePath)
```


---


### GetComprehensiveHealthReport


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ComprehensiveHealthReport> GetComprehensiveHealthReport(
        List<HealthEngineType>? engines = null,
        string? projectName = null,
        string? filePath = null,
        int offset = 0,
        int limit = 10,
        int timeoutSeconds = 25)
```


---


### GetReverseCallGraph


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ReverseCallGraphNode?> GetReverseCallGraph(string filePath, string methodName, int maxDepth = 3)
```


---


### GetSolutionMetrics


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<SolutionMetrics> GetSolutionMetrics(string? projectName = null)
```


---


### GetSymbolInfo


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<SymbolHoverInfo?> GetSymbolInfo(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
```


---


### MoveFileToNamespaceFolder


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MoveFileToNamespaceFolder(string filePath)
```


---


### PreviewRenameImpact


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<RenameImpactPreview> PreviewRenameImpact(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
```


---


## SentinelModernizationTools


**Type:** Tool Class  

**Tools:** 24


**Tools in this source:**


- AddBraces

- ClassToRecord

- CleanupImplicitSpans

- ConvertStaticToExtension

- ConvertSwitchToExpression

- ConvertToSourceGeneratedLogging

- FixThreadSleep

- MakeClassImmutable

- ModernizeExceptions

- OptimizeIndependentAwaits

- OptimizeToValueTask

- RecordToClass

- SimplifyBooleanExpressions

- SimplifyMemberAccess

- SimplifyVerbosity

- UpgradePatternMatching

- UpgradeThreadSafety

- UpgradeToModernGuards

- UpgradeToPrimaryConstructor

- UpgradeUnboundNameof

- UseExceptionExpressions

- UseFieldBackedProperties

- UseIndexFromEnd

- UseTimeProvider


---


### AddBraces


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddBraces(string filePath)
```


---


### ClassToRecord


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ClassToRecord(string filePath, string className)
```


---


### CleanupImplicitSpans


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> CleanupImplicitSpans(string filePath)
```


---


### ConvertStaticToExtension


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertStaticToExtension(string filePath, string methodName)
```


---


### ConvertSwitchToExpression


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertSwitchToExpression(string filePath, string methodName)
```


---


### ConvertToSourceGeneratedLogging


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertToSourceGeneratedLogging(string filePath, string className)
```


---


### FixThreadSleep


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> FixThreadSleep(string filePath)
```


---


### MakeClassImmutable


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MakeClassImmutable(string filePath, string className)
```


---


### ModernizeExceptions


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> ModernizeExceptions(List<CodeHealingEngine.ExceptionTarget> targets, bool autoStage = true)
```


---


### OptimizeIndependentAwaits


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> OptimizeIndependentAwaits(string filePath, string methodName)
```


---


### OptimizeToValueTask


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> OptimizeToValueTask(string filePath, string methodName)
```


---


### RecordToClass


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RecordToClass(string filePath, string recordName)
```


---


### SimplifyBooleanExpressions


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SimplifyBooleanExpressions(string filePath)
```


---


### SimplifyMemberAccess


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SimplifyMemberAccess(string filePath)
```


---


### SimplifyVerbosity


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SimplifyVerbosity(string filePath)
```


---


### UpgradePatternMatching


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradePatternMatching(string filePath)
```


---


### UpgradeThreadSafety


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradeThreadSafety(string filePath)
```


---


### UpgradeToModernGuards


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradeToModernGuards(string filePath)
```


---


### UpgradeToPrimaryConstructor


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradeToPrimaryConstructor(string filePath, string className)
```


---


### UpgradeUnboundNameof


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpgradeUnboundNameof(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
```


---


### UseExceptionExpressions


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseExceptionExpressions(string filePath, string methodName)
```


---


### UseFieldBackedProperties


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseFieldBackedProperties(string filePath)
```


---


### UseIndexFromEnd


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseIndexFromEnd(string filePath)
```


---


### UseTimeProvider


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UseTimeProvider(string filePath)
```


---


## SentinelQualityTools


**Type:** Tool Class  

**Tools:** 14


**Tools in this source:**


- AddBenchmarkStub

- AddCancellationTokenToMethod

- AddConfigureAwaitFalse

- AddGuardClauses

- AnalyzeMethodControlFlow

- AnalyzeMethodDataFlow

- AnalyzePathCoverage

- ConvertLockToSemaphoreSlim

- ConvertToAsyncEnumerable

- GenerateTestScaffold

- GenerateTestSkeleton

- GetDiagnosticsSummary

- MakeMethodThreadSafe

- RemoveConfigureAwaitFalse


---


### AddBenchmarkStub


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddBenchmarkStub(string filePath, string className, string methodName)
```


---


### AddCancellationTokenToMethod


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddCancellationTokenToMethod(string filePath, string methodName)
```


---


### AddConfigureAwaitFalse


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddConfigureAwaitFalse(string filePath, bool libraryMode = true)
```


---


### AddGuardClauses


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddGuardClauses(string filePath, string methodName)
```


---


### AnalyzeMethodControlFlow


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ControlFlowAnalysisResult> AnalyzeMethodControlFlow(
        string filePath,
        string methodName,
        int? disambiguateLine = null)
```


---


### AnalyzeMethodDataFlow


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DataFlowAnalysisResult> AnalyzeMethodDataFlow(
        string filePath,
        string methodName,
        int? disambiguateLine = null)
```


---


### AnalyzePathCoverage


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<PathCoverageReport> AnalyzePathCoverage(string filePath, string methodName)
```


---


### ConvertLockToSemaphoreSlim


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertLockToSemaphoreSlim(string filePath, string methodName)
```


---


### ConvertToAsyncEnumerable


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertToAsyncEnumerable(string filePath, string methodName)
```


---


### GenerateTestScaffold


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<TestScaffoldResult> GenerateTestScaffold(string filePath, string className)
```


---


### GenerateTestSkeleton


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<TestSkeletonReport> GenerateTestSkeleton(string filePath, string className)
```


---


### GetDiagnosticsSummary


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticsSummaryResult> GetDiagnosticsSummary(
        string? filePath = null, string? projectName = null, int topN = 20)
```


---


### MakeMethodThreadSafe


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MakeMethodThreadSafe(string filePath, string methodName, string lockFieldName = "_lock")
```


---


### RemoveConfigureAwaitFalse


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RemoveConfigureAwaitFalse(string filePath)
```


---


## SentinelRefactoringTools


**Type:** Tool Class  

**Tools:** 61


**Tools in this source:**


- AddAttribute

- AddBaseType

- AddConstructorParameter

- AddEnumValue

- AddField

- AddMemberToClass

- AddModifier

- AddProperty

- AddSummaryComment

- AddUsingDirective

- AnalyzeControlFlow

- AnalyzeDataFlow

- ChangeAccessibility

- ChangeSignature

- ConvertAbstractToInterface

- ConvertExpressionBody

- ConvertMethodToIndexer

- ConvertPropertyToMethods

- ExtensionToStatic

- ExtractConstant

- ExtractInterface

- ExtractMethod

- ExtractSuperclass

- FormatDocumentPreview

- GenerateMapping

- GetById

- InlineField

- InlineMethod

- InlineParameter

- InlineVariable

- InsertMemberAfter

- InsertMemberBefore

- IntroduceField

- IntroduceParameter

- IntroduceParameterObject

- IntroduceVariable

- InvertAssignments

- MakeMethodStatic

- MoveAllTypesToFiles

- MoveAllTypesToFilesInProject

- MoveAllTypesToFilesInSolution

- MoveTypeToFile

- MoveTypeToOuterScope

- OptimizeTaskWait

- PullUpMember

- ReduceBlockDepth

- RemoveAttribute

- RemoveBaseType

- RemoveMember

- RemoveModifier

- RenameSymbol

- ReplaceConstructorWithFactory

- ReplaceMember

- SafeDeleteSymbol

- SortMembers

- SyncInterfaceToImplementation

- SyncTypeAndFilename

- UpdateXmlDocsFromSignature

- WrapInRegion

- WrapInTryCatch

- WrapInUsing


---


### AddAttribute


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddAttribute(string filePath, string targetName, string attributeSource, bool autoStage = true)
```


---


### AddBaseType


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddBaseType(string filePath, string typeName, string baseTypeName, bool autoStage = true)
```


---


### AddConstructorParameter


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddConstructorParameter(string filePath, string className, string paramName, string paramType, string? fieldName = null, bool autoStage = true)
```


---


### AddEnumValue


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddEnumValue(string filePath, string enumName, string valueName, int? explicitValue = null, bool autoStage = true)
```


---


### AddField


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddField(string filePath, string containerName, string fieldName, string fieldType, string accessibility = "private", bool isReadonly = false, bool isStatic = false, string? initializer = null, bool autoStage = true)
```


---


### AddMemberToClass


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> AddMemberToClass(string filePath, string containerName, string newMemberSource)
```


---


### AddModifier


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddModifier(string filePath, string targetName, string modifier, bool autoStage = true)
```


---


### AddProperty


**Purpose:**
...


**Signature:**

```csharp
public async Task<object> AddProperty(string filePath, string containerName, string propertyName, string propertyType, string accessibility = "public", bool hasSetter = true, bool isInit = false, bool autoStage = true)
```


---


### AddSummaryComment


**Purpose:**
...


**Signature:**

```csharp
public async Task<object> AddSummaryComment(string filePath, string targetName, string summaryText, bool autoStage = true)
```


---


### AddUsingDirective


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> AddUsingDirective(string filePath, string namespaceName, bool autoStage = true)
```


---


### AnalyzeControlFlow


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ControlFlowSummary> AnalyzeControlFlow(string filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
```


---


### AnalyzeDataFlow


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DataFlowSummary> AnalyzeDataFlow(string filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
```


---


### ChangeAccessibility


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> ChangeAccessibility(string filePath, string targetName, string accessibility, bool autoStage = true)
```


---


### ChangeSignature


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> ChangeSignature(string filePath, string methodName, int[] newParameterOrder, bool autoStage = true)
```


---


### ConvertAbstractToInterface


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertAbstractToInterface(string filePath, string className)
```


---


### ConvertExpressionBody


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertExpressionBody(string filePath, string memberName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
```


---


### ConvertMethodToIndexer


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertMethodToIndexer(string filePath, string methodName)
```


---


### ConvertPropertyToMethods


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ConvertPropertyToMethods(string filePath, string propertyName)
```


---


### ExtensionToStatic


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ExtensionToStatic(string filePath, string methodName)
```


---


### ExtractConstant


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ExtractConstant(string filePath, string contextSnippet, string constantName, string visibility = "private", string? lineBefore = null, string? lineAfter = null)
```


---


### ExtractInterface


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> ExtractInterface(string filePath, string className, string interfaceName, bool autoStage = true)
```


---


### ExtractMethod


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<ExtractMethodResult> ExtractMethod(
        string filePath, int startLine, string startLineText, int endLine, string endLineText, string newMethodName)
```


---


### ExtractSuperclass


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> ExtractSuperclass(string[] filePaths, string[] classNames, string newBaseClassName, bool autoStage = true)
```


---


### FormatDocumentPreview


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<FormatPreviewResult> FormatDocumentPreview(string filePath)
```


---


### GenerateMapping


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> GenerateMapping(string filePath, string fromType, string toType)
```


---


### GetById


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<Product?> GetById(\". " +
                 "Provide lineBefore and/or lineAfter (verbatim text from the line above/below the target) when the snippet could match multiple locations. " +
                 "Returns an error if the snippet matches zero or multiple locations. " +
                 "Returns per-file diff hunks (before/after for each changed line with ±2 lines of context) plus a staged ChangeId. Review FileChanges before calling ApplyStagedChanges.")]
    public async Task<object> RenameSymbol(string filePath, string symbolName, string contextSnippet, string newName, bool autoStage = true, string? lineBefore = null, string? lineAfter = null)
```


---


### InlineField


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InlineField(string filePath, string fieldName)
```


---


### InlineMethod


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InlineMethod(string filePath, string methodName)
```


---


### InlineParameter


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InlineParameter(string filePath, string methodName, string parameterName)
```


---


### InlineVariable


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InlineVariable(string filePath, string variableName)
```


---


### InsertMemberAfter


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> InsertMemberAfter(string filePath, string containerName, string afterMemberName, string newMemberSource, bool autoStage = true)
```


---


### InsertMemberBefore


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> InsertMemberBefore(string filePath, string containerName, string beforeMemberName, string newMemberSource, bool autoStage = true)
```


---


### IntroduceField


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceField(string filePath, string contextSnippet, string newFieldName, string? lineBefore = null, string? lineAfter = null)
```


---


### IntroduceParameter


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceParameter(string filePath, string contextSnippet, string newParamName, string? lineBefore = null, string? lineAfter = null)
```


---


### IntroduceParameterObject


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceParameterObject(
        string filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null)
```


---


### IntroduceVariable


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> IntroduceVariable(string filePath, string contextSnippet, string newVariableName, string? lineBefore = null, string? lineAfter = null)
```


---


### InvertAssignments


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> InvertAssignments(string filePath, int startLine, int endLine)
```


---


### MakeMethodStatic


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MakeMethodStatic(string filePath, string methodName)
```


---


### MoveAllTypesToFiles


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> MoveAllTypesToFiles(string filePath, bool autoStage = true)
```


---


### MoveAllTypesToFilesInProject


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> MoveAllTypesToFilesInProject(string projectName, bool autoStage = true)
```


---


### MoveAllTypesToFilesInSolution


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> MoveAllTypesToFilesInSolution(bool autoStage = true)
```


---


### MoveTypeToFile


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> MoveTypeToFile(string filePath, string typeName, bool autoStage = true)
```


---


### MoveTypeToOuterScope


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> MoveTypeToOuterScope(string filePath, string typeName)
```


---


### OptimizeTaskWait


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> OptimizeTaskWait(string filePath)
```


---


### PullUpMember


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> PullUpMember(string filePath, string className, string memberName, bool autoStage = true)
```


---


### ReduceBlockDepth


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ReduceBlockDepth(string filePath, string methodName)
```


---


### RemoveAttribute


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> RemoveAttribute(string filePath, string targetName, string attributeName, bool autoStage = true)
```


---


### RemoveBaseType


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> RemoveBaseType(string filePath, string typeName, string baseTypeName, bool autoStage = true)
```


---


### RemoveMember


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> RemoveMember(string filePath, string memberName)
```


---


### RemoveModifier


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> RemoveModifier(string filePath, string targetName, string modifier, bool autoStage = true)
```


---


### RenameSymbol


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> RenameSymbol(string filePath, string symbolName, string contextSnippet, string newName, bool autoStage = true, string? lineBefore = null, string? lineAfter = null)
```


---


### ReplaceConstructorWithFactory


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ReplaceConstructorWithFactory(string filePath, string className)
```


---


### ReplaceMember


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ReplaceMember(string filePath, string memberName, string newSource)
```


---


### SafeDeleteSymbol


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> SafeDeleteSymbol(string filePath, string contextSnippet, bool autoStage = true, string? lineBefore = null, string? lineAfter = null)
```


---


### SortMembers


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> SortMembers(string filePath, string containerName, bool autoStage = true)
```


---


### SyncInterfaceToImplementation


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SyncInterfaceToImplementation(string filePath, string className, string interfaceName)
```


---


### SyncTypeAndFilename


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SyncTypeAndFilename(string filePath)
```


---


### UpdateXmlDocsFromSignature


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> UpdateXmlDocsFromSignature(string filePath, string methodName)
```


---


### WrapInRegion


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> WrapInRegion(string filePath, int startLine, int endLine, string regionName, bool autoStage = true)
```


---


### WrapInTryCatch


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<object> WrapInTryCatch(string filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, bool autoStage = true)
```


---


### WrapInUsing


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> WrapInUsing(string filePath, int startLine, int endLine, string disposalName)
```


---


## SentinelWorkspaceTools


**Type:** Tool Class  

**Tools:** 19


**Tools in this source:**


- ApplyProposedChanges

- ApplyProposedDiff

- ApplyStagedChanges

- CreateProject

- Diagnose

- GetExternalChanges

- GetFileDiagnostics

- GetProjectDiagnostics

- GetSolutionDiagnostics

- GetStagedChanges

- ListDependencies

- LoadSolution

- RetryFailedChanges

- SafeDelete

- SplitProjectByFolder

- SyncTypeAndFilename

- ValidateProposedChanges

- ValidateProposedDiff

- ValidateStagedChanges


---


### ApplyProposedChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<PersistentWorkspaceManager.ApplyChangesResult> ApplyProposedChanges(Dictionary<string, string> changes, int retryCount = 3)
```


---


### ApplyProposedDiff


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> ApplyProposedDiff(string filePath, string unifiedDiff)
```


---


### ApplyStagedChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<PersistentWorkspaceManager.ApplyChangesResult> ApplyStagedChanges(string changeId, int retryCount = 3)
```


---


### CreateProject


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> CreateProject(string projectName, string projectType = "console")
```


---


### Diagnose


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<HealthReport> Diagnose(string? solutionPath = null, bool verbose = false)
```


---


### GetExternalChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public List<string> GetExternalChanges()
```


---


### GetFileDiagnostics


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticSummary> GetFileDiagnostics(string filePath)
```


---


### GetProjectDiagnostics


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticSummary> GetProjectDiagnostics(string projectName)
```


---


### GetSolutionDiagnostics


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticSummary> GetSolutionDiagnostics()
```


---


### GetStagedChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public Dictionary<string, string> GetStagedChanges(string changeId)
```


---


### ListDependencies


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DependencyEngine.ProjectDependencyReport> ListDependencies(string projectName)
```


---


### LoadSolution


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> LoadSolution(string solutionPath)
```


---


### RetryFailedChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<PersistentWorkspaceManager.ApplyChangesResult> RetryFailedChanges(List<string>? specificFiles = null, int retryCount = 3)
```


---


### SafeDelete


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SafeDelete(string filePath, int line, int column)
```


---


### SplitProjectByFolder


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SplitProjectByFolder(string sourceProjectName, string folderName, string targetProjectName)
```


---


### SyncTypeAndFilename


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<string> SyncTypeAndFilename(string filePath)
```


---


### ValidateProposedChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticReport> ValidateProposedChanges(Dictionary<string, string> changes)
```


---


### ValidateProposedDiff


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticReport> ValidateProposedDiff(string filePath, string unifiedDiff)
```


---


### ValidateStagedChanges


**Purpose:**
Code analysis and refactoring tool


**Signature:**

```csharp
public async Task<DiagnosticReport> ValidateStagedChanges(string changeId)
```


---

