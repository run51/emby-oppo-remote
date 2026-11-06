#!/bin/bash
# Builds the APK without Gradle: aapt2 (resources) -> javac -> d8 (dex) -> zipalign -> apksigner.
set -e
cd "$(dirname "$0")"
SDK=$HOME/android/sdk; BT=$SDK/build-tools/35.0.0; JAR=$SDK/platforms/android-34/android.jar
KS=$HOME/.config/oppo-205/android-oppo.jks
# Keystore password: $OPPO_KS_PASS, or the content of ~/.config/oppo-205/keystore.pass
export PASS=${OPPO_KS_PASS:-$(cat "$HOME/.config/oppo-205/keystore.pass" 2>/dev/null)}
[ -n "$PASS" ] || { echo "Set OPPO_KS_PASS (keystore password)"; exit 1; }
rm -rf build && mkdir -p build/res build/gen build/classes build/dex
$BT/aapt2 compile --dir res -o build/res/res.zip
$BT/aapt2 link -I $JAR --manifest AndroidManifest.xml -o build/base.apk --java build/gen build/res/res.zip --auto-add-overlay
javac -nowarn -g --release 8 -classpath $JAR -d build/classes $(find src build/gen -name '*.java') 2>&1 | grep -v "^Note:" || true
$BT/d8 --lib $JAR --min-api 24 --release --output build/dex $(find build/classes -name '*.class')
cp build/base.apk build/unsigned.apk
python3 -I -c "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'a'); z.write(sys.argv[2],'classes.dex'); z.close()" build/unsigned.apk build/dex/classes.dex
$BT/zipalign -f -p 4 build/unsigned.apk build/aligned.apk
if [ ! -f "$KS" ]; then
  keytool -genkeypair -keystore "$KS" -alias oppo -keyalg RSA -keysize 2048 -validity 10000 \
    -storepass "$PASS" -keypass "$PASS" -dname "CN=OPPO Emby, O=Home" >/dev/null 2>&1
  chmod 600 "$KS"
fi
$BT/apksigner sign --ks "$KS" --ks-pass env:PASS --key-pass env:PASS --out build/oppo.apk build/aligned.apk
$BT/apksigner verify build/oppo.apk && ls -la build/oppo.apk
