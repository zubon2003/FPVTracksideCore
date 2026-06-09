# macOS arm64 用 OpenCV dylib 同梱手順

このディレクトリには、Mac (Apple Silicon) でビルドした
`libOpenCvSharpExtern.dylib` と、それが依存する OpenCV 4.11 系の
`libopencv_*.dylib` 一式を **`@loader_path` 参照に書き換えた状態で**
コミットします。

これにより、エンドユーザの Mac に `brew install opencv@4` が無くても
ArUco 検出が動くようになります。

---

## 1. 前提

- Apple Silicon Mac (arm64)
- `brew install opencv@4` 済み（4.11 系。`brew info opencv` で確認）
  - 既に `libOpenCvSharpExtern.dylib` のビルドに使ったものと同一バージョン
- Xcode Command Line Tools (`otool`, `install_name_tool` が使える)
- このリポジトリを clone 済み、ブランチは `aruco-debug` ベースで作業

```bash
cd <repo>/Timing/native/osx-arm64
ls -la libOpenCvSharpExtern.dylib  # 既にコミット済みのものが見えるはず
```

---

## 2. 必要な dylib の特定

`libOpenCvSharpExtern.dylib` がリンクしている OpenCV 本体を確認します。

```bash
otool -L libOpenCvSharpExtern.dylib | grep -i opencv
```

出力例（実際のパスは brew の場所による）:

```
/opt/homebrew/opt/opencv/lib/libopencv_aruco.411.dylib   (compatibility version 4.11.0, current version 4.11.0)
/opt/homebrew/opt/opencv/lib/libopencv_calib3d.411.dylib ...
/opt/homebrew/opt/opencv/lib/libopencv_imgproc.411.dylib ...
/opt/homebrew/opt/opencv/lib/libopencv_core.411.dylib    ...
... (実装次第で他にも features2d / objdetect / video / dnn / flann 等)
```

ここに出てきた **すべての** `libopencv_*.dylib` をこのディレクトリに
コピーする必要があります。

---

## 3. コピー & 推移的依存の取り込み

OpenCV 本体は相互に依存しているので、コピーした各 dylib に対しても
再帰的に `otool -L` を走らせて、不足する `libopencv_*.dylib` が
あれば追加します。さらに `libtbb` / `libpng` / `libjpeg` 等の
非システム依存も含める必要があります（`/usr/lib/` や `/System/`
始まりのものは Mac 標準なので除外）。

```bash
# brew opencv の lib ディレクトリを特定
OPENCV_LIB=$(brew --prefix opencv)/lib
echo "OPENCV_LIB=$OPENCV_LIB"

# 第1段階: libOpenCvSharpExtern.dylib が直接参照する OpenCV を全部コピー
otool -L libOpenCvSharpExtern.dylib \
  | awk '/libopencv_/ {print $1}' \
  | while read ref; do
      cp "$ref" .
    done

# 第2段階: 推移的に必要な dylib を取り込む (固定点に達するまで反復)
prev_count=0
while true; do
  current_count=$(ls libopencv_*.dylib 2>/dev/null | wc -l)
  [ "$current_count" = "$prev_count" ] && break
  prev_count=$current_count

  for f in libopencv_*.dylib; do
    otool -L "$f" \
      | awk '/^[[:space:]]/ {print $1}' \
      | grep -v "^/usr/lib\|^/System\|@loader_path\|@rpath\|@executable_path" \
      | while read ref; do
          name=$(basename "$ref")
          [ -e "$name" ] && continue
          if [ -e "$ref" ]; then
            cp "$ref" .
            echo "added: $name (pulled by $f)"
          fi
        done
  done
done

ls libopencv_*.dylib
```

非 OpenCV の依存（あれば）も同様に手動で取り込みます。代表例:

```bash
otool -L libopencv_*.dylib \
  | awk '/^[[:space:]]/ {print $1}' \
  | grep -v "libopencv_\|^/usr/lib\|^/System\|@loader_path\|@rpath\|@executable_path\|:" \
  | sort -u
# ↑ ここに出たもの (libtbb*, libpng*, libjpeg*, libtiff*, libwebp*, libopenjp2* など) もコピー
```

---

## 4. install_name の書き換え

`@loader_path/` 参照に統一し、エンドユーザ Mac に brew が無くても
同じディレクトリの dylib が使われるようにします。

```bash
# 各 dylib の自己 install_name (LC_ID_DYLIB) を @loader_path/<filename> に
for f in libOpenCvSharpExtern.dylib libopencv_*.dylib; do
  install_name_tool -id "@loader_path/$f" "$f"
done

# 相互参照と libOpenCvSharpExtern → OpenCV 参照を @loader_path/... に書き換え
for f in libOpenCvSharpExtern.dylib libopencv_*.dylib; do
  otool -L "$f" \
    | awk '/^[[:space:]]/ {print $1}' \
    | grep -v "^/usr/lib\|^/System\|^@loader_path\|^@rpath\|^@executable_path\|:" \
    | while read ref; do
        name=$(basename "$ref")
        install_name_tool -change "$ref" "@loader_path/$name" "$f"
      done
done

# 非 OpenCV の bundled deps (libtbb 等) も入れたなら、同様に書き換え
# 上のループの grep から libtbb 等を除外しなければ自動的に対象になる
```

注意:
- 元の dylib に LC_RPATH (`@rpath/...`) 参照がある場合、`install_name_tool -rpath` か
  `-delete_rpath` で整理。最終的に `otool -l <f> | grep -A2 LC_RPATH` を見て brew 系の
  rpath が残っていないことを確認する。
- 書き換え後は **コード署名が壊れる**（ad-hoc 署名状態になる）。配布形態によっては
  `codesign --force --sign - <f>` で ad-hoc 再署名する必要がある。社内配布や開発用なら
  そのままでも動くことが多い。

---

## 5. 検証

### 5.1 install_name に絶対パスが残っていないこと

```bash
otool -L libOpenCvSharpExtern.dylib libopencv_*.dylib \
  | grep -v "@loader_path\|^/usr/lib\|^/System\|:$\|^$" \
  | grep -v "compatibility version"
# ↑ 何も出なければ OK
# brew のパス (/opt/homebrew/... または /usr/local/...) が1行でも残っていたら NG
```

### 5.2 dlopen 単体テスト

```bash
# brew の opencv を一時的にパスから外して、bundled dylib のみで dlopen できるか
DYLD_LIBRARY_PATH="" \
  python3 -c "import ctypes; ctypes.CDLL('./libOpenCvSharpExtern.dylib'); print('OK')"
```

`OK` が出れば成功。エラーが出る場合は、足りない dylib が
メッセージに表示されるので追加コピー&書き換え。

### 5.3 .NET ビルド経由での確認

```bash
cd <repo>
dotnet publish FPVMacSideCore/FPVMacsideCore.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -o bin/Release-publish/osx-arm64

ls bin/Release-publish/osx-arm64/runtimes/osx-arm64/native/
# libOpenCvSharpExtern.dylib と libopencv_*.dylib が全部並んでいれば csproj 反映 OK
```

実行後、`TimingLog.txt` に以下が出れば成功:

```
[ArUco-Debug] native-probe: FOUND   .../runtimes/osx-arm64/native/libOpenCvSharpExtern.dylib ...
[ArUco-Debug] Cv2.GetVersionString() OK, OpenCV 4.11.0
[ArUco-Debug] CvAruco.GetPredefinedDictionary(Dict4X4_50) OK.
```

---

## 6. コミット & PR

```bash
cd <repo>
git checkout -b aruco-bundle-opencv-dylibs origin/aruco-debug

# このディレクトリ配下の dylib を追加
git add Timing/native/osx-arm64/

# libOpenCvSharpExtern.dylib も install_name 書き換えで内容が変わっているので
# stage されることを確認
git status

git commit -m "ArUco: macOS arm64 用 OpenCV dylib を同梱 (@loader_path 参照に統一)"
git push origin aruco-bundle-opencv-dylibs
```

PR は `zubon2003/FPVTracksideCore` の `aruco-debug` ブランチ宛にお願いします。

csproj 側 (`Timing/Timing.csproj`) は既に `native/osx-arm64/*.dylib` を
glob で同梱する設定にする想定なので、ファイル名固定の編集は不要です
（csproj の glob 化は PR 受領前後のどちらでも対応可能）。

---

## 7. 困ったときに見るもの

- `otool -L <dylib>` — 参照先（install_name と LC_LOAD_DYLIB）
- `otool -D <dylib>` — 自分自身の LC_ID_DYLIB
- `otool -l <dylib> | grep -A2 LC_RPATH` — rpath 一覧
- 実行時の dlopen 失敗ログ: 起動コマンドに `DYLD_PRINT_LIBRARIES=1` を
  付けて起動すると、どの dylib をどの順でロードしようとして失敗したか
  tty に流れる
- `codesign -dv <dylib>` — 署名状態の確認

---

## 8. ファイルサイズ目安

参考までに、brew opencv@4.11 のサイズ感:

- `libopencv_core.411.dylib` ≈ 8 MB
- `libopencv_imgproc.411.dylib` ≈ 5 MB
- `libopencv_aruco.411.dylib` ≈ 200 KB
- `libopencv_calib3d.411.dylib` ≈ 2 MB
- `libopencv_features2d.411.dylib` ≈ 800 KB
- 全部合計でおおむね **20〜40 MB** 程度になります（dnn を含めると +60MB）。

`dnn` / `videoio` / `gapi` など ArUco に不要なモジュールは
`otool -L libOpenCvSharpExtern.dylib` に出てこなければ同梱不要なので、
最終的には 20MB 前後に収まるはずです。
