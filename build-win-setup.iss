; 猫爪影视 Windows 安装程序脚本（Inno Setup 6.3+/7）
; 用法: "C:\Users\lvjin\InnoSetup\ISCC.exe" build-win-setup.iss
;       或  .\build-win-release.ps1  （一键：publish + 编译安装包）
; 打包源: CatClawVideo.Maui\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish（self-contained 绿色目录发布）
; 依赖: installer\vc_redist.x64.exe（微软官方的 VC++ 2015-2022 x64 可再发行组件，约 24MB）——
;       随包的文件里有 8 个原生 DLL 依赖它（FFmpeg 系 + FFmpegInteropX），
;       已在系统里装过则跳过（见下方 [Code] VCRedistPresent）。
#define MyAppName "猫爪影视"
#ifndef MyAppVersion
#define MyAppVersion "0.2.5"
#endif
#ifndef MyPublishDir
#define MyPublishDir "CatClawVideo.Maui\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish"
#endif
; 随包 Android 运行环境（模拟器 + 系统镜像 + adb）。由 build-win-release.ps1 用
; /DMyAndroidSdk=<路径> 传入（它会先校验必要文件在不在）；这里的默认值只是让
; 单独手跑 ISCC 时也有个合理落点。
#ifndef MyAndroidSdk
#define MyAndroidSdk "C:\Users\lvjin\AppData\Local\Android\Sdk"
#endif
#define MyAppPublisher "CatClawVideo"
#define MyAppExeName "CatClawVideo.Maui.exe"
#define MyAppId "7A4E1D93-6B25-4F08-9C71-CatClawVideoWin"
[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\CatClawVideo
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=CatClawVideo.Maui\Resources\AppIcon\appicon.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=release\windows
OutputBaseFilename=catclaw.video-{#MyAppVersion}-Setup
LicenseFile=LICENSE
PrivilegesRequired=admin
[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加图标:"; Flags: unchecked
[Files]
; ★ Excludes 里的后四项是 2026-09-22 用 A/B 实测挑出来的（同一 publish 目录重压对比）：
;   · *.lib —— 链接期文件，运行时完全不用（9 个 / 1.4MB 原始）
;   · onnxruntime.dll + DirectML.dll + Microsoft.ML.OnnxRuntime.dll + Microsoft.Windows.AI.*
;     —— Windows App SDK 的 **Windows AI 组件**，随包带入但本 App 代码里零引用
;     （grep onnx|DirectML|Windows.AI 无命中）。共 25 个文件 / 43.3MB 原始。
;   实测收益：安装包 251.3MB → 238.0MB，**省 13.3MB（5.3%）**。
;   ⚠ 另测过再排掉 ThunderRuntime 的 libgtk-3-0/libaom/libSvtAv1Enc-4/libjxl（`-nographic` 看似不用），
;     只再多省 7.9MB，而它们在 QEMU 的导入表里、被加载期硬要求，收益不抵风险 ⇒ **不排**。
; ★ 瘦身批次 2（2026-10-13）：OpenTK.*.dll + Silk.NET.*.dll 是 **libmpv 后端**（GL 上下文 +
;   D3D11 互操作）的独占依赖，而 mpv-2.dll 自 0.2.5 起已不随包 ⇒ MpvBackendAvailable() 恒为
;   false，SetSourceWithDetectionAsync 里的 `MpvBackendAvailable() && (...)`
;   短路求值永远不会走到 DolbyVisionDetector/ActivateMpv。
;   取证：运行中的宿主进程模块表里**没有**任何 OpenTK/Silk.NET/mpv 条目。原始约 40MB。
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml,*.lib,onnxruntime.dll,DirectML.dll,Microsoft.ML.OnnxRuntime.dll,Microsoft.Windows.AI.*,OpenTK.*.dll,Silk.NET.*.dll"
; ★ 瘦身批次 1（2026-09-29，在 csproj 侧 Content Remove，不占本文件 Excludes）：
;   QemuGuest\qemu-system-x86_64.exe（24.3MB）——x86 mini guest 实验专用，三件套校验
;   本就过不了（x86guest\ 内核+initrd 未随包），运行时安全回落宿主 JRE 桥。
;   实测：publish 目录 778.7→754.4MB（-24.3MB 全额兑现）；但**安装包只 374.3→373.1MB（-1.2MB）**——
;   根因：SolidCompression=yes 下 x86 引擎与 aarch64 引擎同源构建、内容高度重合，
;   solid LZMA 已把相似数据去重，删掉第二份的增量收益天然小。
;   ⇒ 教训：solid 压缩下，批次裁剪要优先挑「不与随包其它内容相似」的文件才有实际收益。
;   （同日撤销：x86 mini guest 转正，引擎与 x86guest\ 恢复随包 +310MB——本条留作教训存档。）
; VC++ 2015-2022 运行库（x64）。放到 {tmp} 并在装完后删除，不往用户机器上留垃圾文件。
; ⚠ 必须是**完整包**（约 24MB），不能是 VS Package Cache 里那种 600KB 的下载器桩（离线装不上）。
Source: "installer\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

; ═══════════════════════════════════════════════════════════════════════════
; Android 运行环境（模拟器 + 系统镜像 + adb）
;
; ★ 0.2.6 起默认**不随包**（轻量包 ~130MB）：运行环境发布在
;     https://github.com/kankejiang/CatClaw.Qemu/releases（tag android-runtime-v1）
;   首次使用 jar 源时由客户端提示下载解压到 %LOCALAPPDATA%\CatClawVideo\AndroidSdk
;   （见 Services/RuntimeProvisioner.cs；网页源 / MacCMS / JS 源不受影响）。
;   要出「完整离线包」（给离线机器/批量部署）时加编译开关：
;     ISCC /DMyBundleRuntime=1 build-win-setup.iss
;
; 目录刻意与 Android SDK 同构（随包/下载两处布局相同）：
;   ...\AndroidSdk\emulator\emulator.exe
;   ...\AndroidSdk\platform-tools\adb.exe
;   ...\AndroidSdk\system-images\android-36\google_apis_playstore\x86_64\
; 因为 emulator.exe 把「自己的上一级目录」当作 SDK 根，AVD 里那句相对路径
; image.sysdir.1 就相对它解析 ⇒ 整包自洽，不碰注册表/环境变量。
; （2026-10-12 用 junction 复刻该布局冷启实测：76s 开机、native bridge 生效。）
;
; ⚠ system.img 与 VerifiedBootParams.textproto 是**强耦合的一对**：
;   前者被精简并重签过（vbmeta 摘要变了），后者带着配套的
;   androidboot.vbmeta.{size,hash_alg,digest}。必须同时替换，否则 init 报
;   "vbmeta digest error isn't allowed" 直接起不来。
;   ⇒ 既不能只排除其中一个，也绝不能把 *.orig 备份打进包。
; ═══════════════════════════════════════════════════════════════════════════

#ifdef MyBundleRuntime

; 模拟器本体（已剔除非 x86_64 的 qemu 引擎，1033MB → 663MB）
; ★ 瘦身批次 2（2026-10-13，实测取证）：应用始终以 -no-window 拉起模拟器，此时**实际跑的是
;   qemu-system-x86_64-headless**，整个 Qt UI 栈根本不被加载。取证方法：模拟器起来后 dump
;   进程模块表 —— emulator.exe + qemu-system-x86_64-headless 一共只加载 132MB，且**没有任何
;   进程加载过 lib64\qt\ 里的任何一个 DLL**。据此排除下列四项（原始 376MB）：
;     · lib64\qt\*                        243.0MB  Qt UI（含 116MB 的 Qt6WebEngineCoreAndroidEmu）
;     · qemu\windows-x86_64\qemu-system-x86_64.exe  62.9MB  只跑 -headless 那个
;     · lib64\vulkan\libvulkan_lvp.dll     48.4MB  软件 Vulkan（走的是 vk_swiftshader）
;     · lib64\gles_angle\*                 22.1MB  ANGLE（-gpu swiftshader_indirect 不走它）
;   裁剪后模拟器目录 663MB → 207MB。**起机实测通过**：用裁掉这四项的树单起一个 AVD，
;   81 秒 boot_completed=1，ro.dalvik.vm.native.bridge=libndk_translation.so 正常。
;   ⚠ 刻意**不**排除 netsimd.exe / nimble_bridge.exe（32MB）：它们是模拟 WiFi/BT 的网络仿真，
;     而 jar 爬虫要在 guest 里真联网取站点数据 —— 为 10MB 上下的压缩收益赌网络栈不划算。
;   ⚠ 排除项必须靠模块表取证，不能靠"看着像没用"：gles_swiftshader、vk_swiftshader、
;     libgfxstream_backend、libandroid-emu-* 都在加载表里，动不得。
Source: "{#MyAndroidSdk}\emulator\*"; DestDir: "{app}\AndroidSdk\emulator"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "*.orig,drivers\*,include\*,qsn.exe,emulator-check.exe,lib64\qt\*,lib64\gles_angle\*,lib64\vulkan\libvulkan_lvp.dll,qemu\windows-x86_64\qemu-system-x86_64.exe"
; adb 三件套（platform-tools 里其余是 fastboot 等，桌面端用不到）
Source: "{#MyAndroidSdk}\platform-tools\adb.exe"; DestDir: "{app}\AndroidSdk\platform-tools"; Flags: ignoreversion
Source: "{#MyAndroidSdk}\platform-tools\AdbWinApi.dll"; DestDir: "{app}\AndroidSdk\platform-tools"; Flags: ignoreversion
Source: "{#MyAndroidSdk}\platform-tools\AdbWinUsbApi.dll"; DestDir: "{app}\AndroidSdk\platform-tools"; Flags: ignoreversion
; 系统镜像。solidbreak：另起一个 solid 块 —— 它与前面那些 PE/DLL 毫无相似度，
; 合在一块只会让 ISCC 为了 2.2GB 数据吃光内存而换不来压缩收益。
Source: "{#MyAndroidSdk}\system-images\android-36\google_apis_playstore\x86_64\*"; \
  DestDir: "{app}\AndroidSdk\system-images\android-36\google_apis_playstore\x86_64"; \
  Flags: ignoreversion recursesubdirs createallsubdirs solidbreak; \
  Excludes: "*.orig"
#endif   ; MyBundleRuntime
[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Run]
; 运行库先装（缺则装、有则跳过），再让用户选是否启动应用。
; 不带 postinstall ⇒ 在安装过程中静默执行（用户无需勾选）；/norestart 交回安装程序处理重启。
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; \
  StatusMsg: "正在安装运行库（Visual C++ 2015-2022 x64）…"; Flags: runhidden; Check: NeedsVCRedist
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}(&R)"; Flags: nowait postinstall skipifsilent
[UninstallDelete]
Type: filesandordirs; Name: "{app}"
; 随包环境的 AVD 家目录（%LOCALAPPDATA%\CatClawVideo\avd）：模拟器在里面生成
; ram.img / userdata-qemu.img / cache.img，冷启后约 3.2GB，纯 scratch，删掉可重建。
; 应用自身的配置与登录态在 %APPDATA%\CatClawVideo，**不动**。
Type: filesandordirs; Name: "{localappdata}\CatClawVideo\avd"

[Code]
// ═══════════════════════════════════════════════════════════════════════
// VC++ 2015-2022 运行库（x64）—— 为什么非要装
//   实测产物的 PE 导入表，随包里 8 个原生 DLL 依赖它：
//     FFmpegInteropX.dll → CONCRT140 / MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1
//     avcodec-62 / avdevice-62 / avformat-62 / avutil-60 / swresample-6 / swscale-9 → VCRUNTIME140
//     avfilter-11        → MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1
//   而 Windows App SDK 的原生库**不需要**它（只依赖系统自带的 UCRT：api-ms-win-crt-*），
//   所以这一项只决定「能不能播放视频」。
//   缺它时干净 Windows 上加载 FFmpeg 会失败 ⇒ 播放不通；开发机因装过 VC++ 不会暴露。
// ═══════════════════════════════════════════════════════════════════════
// ⚠ Inno 的 Pascal Script 不支持函数内的 `const` 段（编译报 "BEGIN expected"），只能用 var。
function RegDwordOr0(const SubKey: String; const Name: String): Cardinal;
var
  V: Cardinal;
begin
  if RegQueryDWordValue(HKEY_LOCAL_MACHINE, SubKey, Name, V) then
    Result := V
  else
    Result := 0;
end;

// 用「注册表 Installed=1 且 Minor >= 29」判定，而不是去测 VCRUNTIME140_1.dll 是否存在：
//   · Minor >= 29 对应 VS2019（14.29）—— 从那一版起才有 VCRUNTIME140_1.dll，
//     能把「只装过 VS2015（14.0）」的机器也归为「需要装」；
//   · 不碰 %System32% 路径是因为本安装包在 32 位进程下测 System32 会被 WOW64 重定向到 SysWOW64，
//     要么用 {sysnative}（旧版 Inno 没有），要么就绕开 —— 注册表判定没有这个坑。
// 两个视图都查：64 位进程看前一个键，万一被重定向还有 WOW6432Node 兜底。
function VCRedistPresent: Boolean;
var
  K64, K32: String;
begin
  K64 := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  K32 := 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  Result :=
    ((RegDwordOr0(K64, 'Installed') = 1) or (RegDwordOr0(K32, 'Installed') = 1))
    and ((RegDwordOr0(K64, 'Minor') >= 29) or (RegDwordOr0(K32, 'Minor') >= 29));
end;

function NeedsVCRedist: Boolean;
begin
  Result := not VCRedistPresent;
end;
