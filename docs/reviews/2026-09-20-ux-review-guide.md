# UniRoboLab 初見 UX レビュー手順書 (2026-09-20)

対象読者: レビューを担当するエージェント (または初めて触る人)。UniRoboLab の設計は知らない前提で書いてある。

## 1. 目的

「URDF を持っているがロボット学習の経験は浅い利用者」が、**チュートリアルだけを頼りに** ① ロボット → ⑥ 実機へ
を通せるかを確かめる。見つけてほしいのは次の 3 種類。

1. **止まる**: 先に進めない、何を押せばよいか分からない、失敗したのに理由と次の一手が出ない
2. **誤解する**: 文言・数値・3D 表示の意味が取れない、取り違える (単位、座標系、成功の定義)
3. **戻れない**: 途中でやめて再開したとき、上流を変えたとき、失敗したときに状態が分からない

方策の精度 (成功率が何 % か) は評価の対象外。**コードの修正はしない**。見つけたことを書くのが成果物。

## 2. 事前に知っておくこと (これ以上の内部知識は使わない)

- リポジトリ: `/home/hijikata/irlab_ws/unirobolab`。利用者に渡す文書は `docs/tutorial.md` と、リリース zip の
  `release/README-ja.md`。**判断の根拠はこの 2 つだけ**にする。`docs/architecture.md` や `docs/ux-flow.md` は
  設計者向けなので読まない (読むと「知っているから分かる」になる)。
- 起動: `scripts/unirobolab_gui.sh <プロジェクトのフォルダ>`。窓は 1280x800、DISPLAY は `:1`。
- Python 環境 (`.venv`) は用意済み。ROS 2 はこの PC には無く、⑤ はコンテナ (`scripts/sim2sim_container.sh start`)
  で動く。イメージは作成済み。
- 学習サーバはポート 10100。**プレイヤーは同時に 1 つだけ起動する** (2 つ目はポートが衝突する)。
- 学習は 1 本あたり数分〜30 分かかる。待つ間に別のことをしてよいが、学習中のプレイヤーは止めない。

## 3. 環境の準備 (レビュー開始時に一度)

```bash
cd /home/hijikata/irlab_ws/unirobolab
pgrep -fa "UniRoboLab.x86_64" | head          # 何も出ないこと (出たら担当者に確認。勝手に kill しない)
docker ps --format '{{.Names}}' | grep unirobolab-sim2sim || scripts/sim2sim_container.sh start
mkdir -p ~/unirobolab_review
scripts/project_init.sh ~/unirobolab_review/servo  python/tests/fixtures/servo_demo.urdf       ServoDemo
scripts/project_init.sh ~/unirobolab_review/arm2   python/tests/fixtures/arm2_planar.urdf      arm2
scripts/project_init.sh ~/unirobolab_review/diffbot python/tests/fixtures/diffbot_nosensors.urdf diffbot
```

`project_init.sh` は「① で URDF を選んだ直後」の状態を作るだけ (利用者が GUI でファイルを選ぶ操作の代わり)。
それ以外の設定は GUI で行う。

## 4. 操作のしかた

窓を直接操作できる場合はそれが最良。できない場合は、フェーズを指定して起動し、画面を撮って読む。

```bash
# 画面を撮る: <プロジェクト> <フェーズ 1-6> <png@秒[,png@done]> [主操作] [制限秒]
scripts/wizard_capture.sh ~/unirobolab_review/servo 1 /tmp/r/servo_1.png@12
scripts/wizard_capture.sh ~/unirobolab_review/servo 2 /tmp/r/servo_2.png@12
scripts/wizard_capture.sh ~/unirobolab_review/servo 2 /tmp/r/servo_2next.png@20 next     # 「次へ」= 契約と学習設定を生成
scripts/wizard_capture.sh ~/unirobolab_review/servo 3 /tmp/r/servo_3a.png@15,/tmp/r/servo_3b.png@done train 900
scripts/wizard_capture.sh ~/unirobolab_review/servo 4 /tmp/r/servo_4.png@25 run
scripts/wizard_capture.sh ~/unirobolab_review/servo 5 /tmp/r/servo_5.png@15,/tmp/r/servo_5b.png@done check 400
scripts/wizard_capture.sh ~/unirobolab_review/servo 6 /tmp/r/servo_6.png@15,/tmp/r/servo_6b.png@done deploy 120
```

- 主操作 (`load` `next` `train` `run` `check` `deploy`) はそのフェーズの主ボタンを押すのと同じ。
- 画面は `Read` で見る。**状態行 (最下段) と、押せないボタン、「!」の付いた段階**を必ず読む。
- ② の設定を変えて試すには `task.json` を直接編集してよい (GUI の入力欄と同じ項目)。ただし「GUI から
  この値にたどり着けるか」を必ず併記する。
- 詳細ログ: プロジェクトのフォルダの `train.log`、`check_runner.log`、`sim2sim_out/report.json`。
  プレイヤーのログは `wizard_capture.log`。

## 5. シナリオ (3 本、必ず全部)

| # | ロボット | ② で選ぶタスク | 期待する結末 |
|---|---|---|---|
| A | servo (固定基体、関節 2) | 関節を目標角へ (既定) | ⑤ PASS、⑥ で DEPLOY.md とパッケージ |
| B | arm2 (固定基体、平面腕) | 物体を所定の場所へ。開始 (0.55, 0.15)、目標 (0.30, 0.45) 大きさ 0.12、許容 0.06、制限時間 4 s | ③ の成功率 50〜70 % 程度、⑤ は一部不合格でもよい |
| C | diffbot (移動基体) | 所定の場所へ (自動で選ばれる) | ⑤ PASS |

B は学習が長い (30 分前後)。A → C → B の順で進め、B の学習中に A・C の記録を書くとよい。

各シナリオで、①〜⑥ のフェーズごとに次を記録する。

- **入った瞬間に分かるか**: 何をすべきか、何が済んでいるか (ステッパー、状態行)
- **文言**: 意味が取れない語、単位や座標系の不明、日本語として不自然な箇所
- **3D**: 表示 (緑の弧・箱・環・球、橙の物体) が判断に足りるか。何を表しているか説明できるか
- **失敗の見え方**: わざと 1 回失敗させる (下の 6 章) → 状態行に理由と次の一手が出るか
- **戻る/再開**: 一度プレイヤーを終了して同じコマンドで起動 → どこから再開したか。② の値を変える → 下流に「!」が付くか

## 6. わざと起こす失敗 (各 1 回、A で行う)

1. ② で許容誤差を極端に小さく (0.005 rad) して学習 → 目標に届かないときの ③ の文言
2. ⑤ をコンテナを止めた状態で実行 (`scripts/sim2sim_container.sh stop`) → 案内が出るか、「コンテナを起動」で復帰するか
3. ④ を学習前に開く → 押せない理由が出るか
4. ⑥ を ⑤ 不合格のまま開く → 進めるか、何と言われるか

## 7. 記録の形式

`docs/reviews/2026-09-20-ux-review-result.md` に書く。1 件 1 行の表を基本にし、必要なら画面のファイル名を添える。

| # | シナリオ/フェーズ | 種類 (止まる/誤解/戻れない/文言/その他) | 重さ | 何が起きたか (事実) | 期待していたこと | 画面 |
|---|---|---|---|---|---|---|
| 1 | A/② | 誤解 | 中 | 「目標は ±0.5 rad から抽選」の rad が何か分からない | 度数併記 | servo_2.png |

重さ: **高** = 先に進めない / 誤った理解のまま実機へ行きかねない、**中** = 迷うが進める、**低** = 文言・見た目。

表のあとに、次の 3 段落を書く。

1. 3 シナリオそれぞれの結末 (どこまで通ったか、⑤ の合否、かかった時間)
2. 「初見で最も困った 3 点」とその理由
3. 逆に「これは分かりやすかった」点 (直すべきでない所を守るため)

## 8. してはいけないこと

- コードや文書を書き換えない (task.json の編集は可)。`git commit` / `git push` はしない
- `pkill` で広いパターンを使わない (学習中のプロセスを巻き込む)。止めるのは自分が起動したプレイヤーだけ
- プレイヤーを 2 つ同時に起動しない。学習中 (③) は別フェーズの撮影で新しいプレイヤーを起動しない
- 既知の制限は再報告しない: 把持は未対応、条件は 1 つ、Windows は未検証、⑤ は Linux のみ、
  学習の成功率は 40 エピソードで ±10 % ばらつく、① の関節名は表示のみで直せない

## 9. 時間の目安

環境準備 10 分、A 40 分 (学習 5 分)、C 40 分 (学習 5〜10 分)、B 70 分 (学習 30 分)、失敗シナリオ 30 分、記録 40 分。
合計 4 時間弱。途中で打ち切る場合も、7 章の 3 段落は必ず書く。
