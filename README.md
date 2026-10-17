# Harmony Startup Optimizer

Harmony共通の型探索をキャッシュし、同じ型を繰り返し探すMODの起動処理を高速化する独立したMODです。

- MOD表示名: `Harmony Startup Optimizer`
- フォルダ / DLL: `HarmonyStartupOptimizer` / `HarmonyStartupOptimizer.dll`
- 内部識別子 / Harmony owner: `mebiustos.harmonystartupoptimizer`
- バージョン: `0.1.2`
- 必須: Anchor Chain
- BDCMおよびStartup Diagnosticsへの実行時依存はありません。

## 初版の対象

ゲームに読み込まれた共通Harmonyの`HarmonyLib.AccessTools.TypeByName(string)`を対象にします。
対象はこの関数を呼ぶすべてのMODです。呼び出し元のMOD名やBDCMの内部メソッドを識別しません。
ゲームやMODがこの関数を経由せず独自に行う探索、フィールド・プロパティ・メソッド探索、
パッチ適用処理、ゲーム処理そのものの実行結果は対象外です。

同一の文字列に対する成功した型探索の結果を、現在のゲームプロセス内で共有します。
初回は元の探索処理をそのまま実行し、型が見つかった場合のみキャッシュします。
文字列は大文字小文字・名前空間・アセンブリ指定を含めて区別します。
未発見結果と例外をキャッシュしないため、後から見つかる型は再探索できます。
アセンブリ追加を検知したらキャッシュを全破棄し、追加前に始まった探索結果も保存しません。
対象関数に他のパッチが追加・解除されてHarmonyが関数を再構築した場合も、変更前の探索結果を破棄します。
キャッシュは起動後もプロセス内で利用し、終了時に破棄します。型探索結果をディスクへ永続保存しません。

初めて探す型の探索費用は引き続き発生します。追加の短縮量は実際の有効MOD構成で測定してください。
BDCM専用版で得られた短縮量が、汎用版の追加効果を示すものではありません。

## 他のHarmonyパッチとの併用

キャッシュによる早期返却はTypeByNameの原処理の内部へ挿入します。
キャッシュヒット時も、TypeByNameに登録された他のMODのprefix・postfixは通常どおり実行されます。
他のprefixが変えた後の引数をキーに使い、他のpostfixが加工する前の元のTypeを保存します。
そのため、保存済みの戻り値へpostfixの加工が二重に適用されることを避けます。
別のprefixが原処理を省略した場合、このキャッシュの探索処理も実行されません。
別のパッチが例外を発生させた場合も元の例外を伝え、失敗した呼び出しから新しい結果を保存しません。

キャッシュはスレッド間で同期し、再帰呼び出しの処理状態はスレッドごとに管理します。
同じ型の最初の探索が同時に走った場合は、複数の元の探索が実行されることを許容します。
他のパッチの導入・解除、結果の同一性、例外、アセンブリ追加などを実際のゲーム付属Monoで検証します。
API不一致やパッチ導入・解除の失敗はエラーログとして記録します。

## 導入

Sea Powerを終了し、配布用の`dist/HarmonyStartupOptimizer`フォルダを
ゲームの`Sea Power_Data/StreamingAssets/`へコピーします。
最終配置は`Sea Power_Data/StreamingAssets/HarmonyStartupOptimizer/HarmonyStartupOptimizer.dll`です。

MODマネージャーでAnchor ChainとHarmony Startup Optimizerを有効にして再起動します。
有効化前に発生した型探索へは遡って適用されません。有効・無効の切り替えは次回起動に反映します。

比較計測時は旧`BDCM Startup Optimizer`を無効にしてください。
専用版が先に探索を省略すると、汎用版単独の効果や全体の呼び出し回数を評価できなくなります。
このMODは旧MODの設定やファイルを自動変更しません。

Startup Diagnosticsは任意です。診断MODを有効にしておくと、既存の起動処理・BDCM初期化・Check時間を比較できます。
汎用版ではキャッシュヒットもTypeByNameへの呼び出しとして診断されるため、
専用版と異なり診断MODの探索回数はヒット分も含みます。所要時間と汎用版のヒット数を合わせて評価してください。

## 起動中の動作表示

画面下中央に `Harmony Startup Optimizer | Active | Cache hits: 12,345` の形式で表示します。
`Active`はキャッシュの導入成功、`Cache hits`はキャッシュを再利用して正常終了した呼び出しの累計です。
初回の探索、未発見、例外、他のパッチが探索を省略した呼び出しはヒットに含めません。
ロード画面では再利用回数が0の間は `Harmony Startup Optimizer | Active` だけを表示し、1以上なら回数も表示します。
メインメニューでは、0でも `Cache hits: 0` を表示します。

位置は現在の画面サイズから計算して下中央に揃え、文字サイズと下端の余白も解像度に応じて調整します。
ロード画面とメインメニューで同じ位置を使い、文字の高さに合わせた表示領域を下端へ寄せ、小さな余白を残します。
表示内容とメニュー状態の確認は毎秒最大4回です。ゲームが重い処理で画面を更新できない間は数字も更新されません。
ロード画面が閉じ、メインメニューが表示されてから3秒で表示だけを削除します。
以後もキャッシュは有効なままです。表示に失敗した場合は共通ログにエラーを記録し、キャッシュは継続します。
Startup Diagnosticsは必要ありません。

### オーバーレイの設定

表示を無効にする場合は、ゲームフォルダー内の `BepInEx/config/mebiustos.harmonystartupoptimizer.cfg` を手動で作成し、次の内容を記述します。

```ini
[Overlay]
Enabled = false
```

設定ファイルがない場合、または `Enabled = true` の場合は表示が有効です。表示を無効にしてもキャッシュは継続します。

## ログ

`BepInEx/LogOutput.log`の`Harmony Startup Optimizer`を確認します。
有効な場合は起動時に`Active: shared TypeByName cache (Harmony ...)`を1行出力します。
無効な場合は起動時にその旨を1行出力します。
それ以外は、初期化・巻き戻し・終了処理の失敗をエラーとして記録します。
ゲームや他のパッチの元の例外は引き続き呼び出し元へ伝えます。

0.1.1では独自JSON出力、保存先フォルダの作成、定期的な集計とログ出力、
型名ごとの統計保持、探索ごとの時間計測を削除しました。
キャッシュ本体は成功結果と無効化世代、呼び出しの正常終了を管理する情報だけを保持します。

0.1.2では画面表示用の再利用回数だけを追加しました。独自ログファイル、定期的な詳細集計、探索ごとの時間計測は追加しません。

0.1.0が作成した過去の`HarmonyStartupOptimizer/*.summary.json`は自動削除しません。
更新・新規作成もしません。過去の計測結果として扱ってください。
Startup Diagnosticsを有効にしている場合、診断MOD自身のログ出力は別途継続します。

## ビルドと検証

Windows .NET SDK 10を使用します。NuGetパッケージは不要です。
`Directory.Build.local.props`で`SeaPowerDir`と`AnchorChainDir`を指定するか、
`build.ps1`へ`-GameDirectory`と`-AnchorChainDirectory`を渡します。

```powershell
.\build.ps1 -Test
```

純粋なキャッシュの動作テストに加え、Sea Power付属のMonoとHarmony DLLを使い、実際に共通APIへパッチを当てて検証します。
他のprefix・postfix、引数・戻り値の加工、原処理の省略、元の例外、未発見の型、
動的なアセンブリ追加、再帰呼び出し、複数スレッド、所有者ごとの解除を対象とします。
別のtranspilerの追加・解除で探索処理自体が変わった場合、変更前の結果を使わないことも検証します。
ゲームにStartup Diagnosticsが導入済みなら、実際の診断DLLとの両方の導入順序も検証します。
診断DLLがない場合は任意の併用テストだけをSKIPとして表示します。
ゲーム本体の起動やBDCMのキャンペーン初期化はテストから実行しません。

配布用ファイルは`dist/HarmonyStartupOptimizer`に生成します。
ゲームを終了した状態で明示的に導入する場合は、次のコマンドを使用します。

```powershell
.\build.ps1 -Test -InstallLocal
```

## ソースの責務

- `TypeLookupCache.cs`: ゲーム・Harmonyに依存しない、成功結果の共有、無効化、並列制御。
- `LookupHooks.cs`: 共通の型探索APIとの接続。他のパッチとの実行順・引数・戻り値・例外の維持。
- `Plugin.cs`: Anchor Chain、有効状態確認、最小限のログ、ライフサイクル。
- `StartupOverlay.cs`: 起動中の動作表示、解像度への追従、表示だけの終了処理。
- `StartupScreen.cs`: ゲームのメニューとロード画面の状態を読み取る表示終了判定。
- `ModActivation.cs`: Sea PowerのMOD管理状態の読み取り。
- `PatchRegistry.cs`: 自分のHarmonyパッチだけの導入・巻き戻し・解除。

型以外の探索へ広げる場合は、専用のキー・無効化条件を持つキャッシュと接続部分を追加する方針です。
