# vd-switch

Windows の仮想デスクトップをキーシミュレーションを介さず直接切り替えるツール。
タスクトレイ常駐型で、ネットワーク経由のフットスイッチにも対応します。

## 背景・動機

Windows の仮想デスクトップは `Ctrl + Win + 左右キー` で切り替えられますが、**mstsc.exe（リモートデスクトップ接続）** などのアプリにフォーカスがあるとショートカットが奪われて機能しません。Logi Options+ でマウスのサイドボタンに割り当てても、キー入力シミュレーション経由である限り同じ問題が生じます。

根本的な解決には、キーシミュレーションを介さず **仮想デスクトップ API を直接呼び出す** 必要があります。Windows は公式のパブリック API を提供していないため、Shell 内部の COM インターフェースを利用します。

## 仕組み

[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) の COM インターフェース定義（Windows 11 24H2 以降対応版）をソースとして取り込み、外部 DLL への依存なしに仮想デスクトップを操作します。

- 外部 DLL（VirtualDesktopAccessor.dll 等）不要
- `vd-switch.exe` 単体で動作
- .NET 9 self-contained ビルド（ランタイムのインストール不要）

## 動作要件

- Windows 11 24H2 以降

## 使い方

### トレイ常駐モード（引数なし）

```
vd-switch.exe
```

タスクトレイにアイコンが表示されます。

- **右クリックメニュー**: Desktop 1 / Desktop 2 / Desktop 3 で即時切り替え、Quit で終了
- **フットスイッチ連携**: `vd-switch-sender`（後述）から IPv6 マルチキャストでパケットを受信し、自動でデスクトップを切り替えます

| パケット | 動作 |
|---|---|
| `a` | Desktop 1（デスクトップ 0）へ |
| `b` | Desktop 2（デスクトップ 1）へ |
| `c` | Desktop 3（デスクトップ 2）へ |

### CLI モード（引数あり）

```
vd-switch <番号>      # 指定した番号のデスクトップに切り替え（0始まり）
vd-switch --current   # 現在のデスクトップ番号を表示
vd-switch --count     # デスクトップの総数を表示
```

## フットスイッチ連携（vd-switch-sender）

Linux マシン（Raspberry Pi 等）に USB フットスイッチを接続し、`sender/vd-switch-sender.py` を使って IPv6 リンクローカルマルチキャストでコマンドを送信できます。

```
# sender/ディレクトリで
make install
systemctl enable --now vd-switch-sender
```

フットスイッチのキー a/b/c をそれぞれ Desktop 1/2/3 に対応させます。キーの意味は受信側（vd-switch.exe）が決定します。

### マルチキャスト仕様

| 項目 | 値 |
|---|---|
| アドレス | `ff12::7664:7377` |
| ポート | `5356` |
| プロトコル | IPv6 リンクローカルマルチキャスト UDP |
| ペイロード | `a` / `b` / `c`（UTF-8 1バイト） |

## ビルド方法

.NET 9 SDK が必要です。Linux からのクロスコンパイルも可能です。

```
dotnet publish -c Release
```

成果物は `bin/Release/net9.0-windows/win-x64/publish/vd-switch.exe` に出力されます。

## 注意事項

Windows の仮想デスクトップ内部 COM インターフェースは非公式であり、**Windows Update によって動作しなくなる可能性があります**。Windows のメジャーアップデート後に動作しなくなった場合は、[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) の最新版を確認してください。

## ライセンス

COM インターフェース定義部分は [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop)（MIT License）に基づきます。詳細は [LICENSE](LICENSE) を参照してください。
