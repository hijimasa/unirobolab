# unirobolab-trainer

UniRoboLab の学習バックエンド。PyPI には公開しない。Unity バイナリが同梱の `uv` で
この git リポジトリを指定して隠しディレクトリに展開する。

- torch は GPU(CUDA / ROCm / CPU)に応じてホイールを切り替える。依存の宣言は
  バックエンド選択が固まってから追加する
- 入力は `contract/` のポリシー契約。観測・行動の組み立てはここでも契約から生成する
