from pathlib import Path
import hashlib
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
INSTALLER = ROOT / "installer"
APP = ROOT / "apps/windows/NektronMoments"


def test_installer_has_its_own_stable_identity_and_user_scope():
    text = (INSTALLER / "NektronMoments.iss").read_text()
    assert "{7A13CB58-5B03-4B35-A2F4-7BC06610ED38}" in text
    for sibling in ("D65BD9AB", "80A2A1BF", "FB805D64"):
        assert sibling not in text
    assert "PrivilegesRequired=lowest" in text
    assert r"DefaultDirName={localappdata}\Programs\Nektron Moments" in text
    assert "ChangesAssociations=no" in text
    assert "ChangesEnvironment=no" in text
    assert "[UninstallDelete]" not in text


def test_installer_keeps_family_visual_style_and_minimal_pages():
    text = (INSTALLER / "NektronMoments.iss").read_text()
    for fragment in (
        "WizardStyle=modern light hidebevels includetitlebar", "WizardBackColor=#F7FAFC",
        "DefaultDialogFontName=Segoe UI", "DisableDirPage=yes", "DisableProgramGroupPage=yes",
        "WizardBackImageFile=", "WizardImageFile=", "WizardSmallImageFile=",
    ):
        assert fragment in text


def test_installer_does_not_bundle_or_trigger_library_ingestion():
    text = (INSTALLER / "NektronMoments.iss").read_text()
    assert "import cli.nektron_moments_cli.desktop_bridge" in text
    assert "--with-enrichment" not in text
    assert "cli.sh sync" not in text
    assert "OpenAI" not in text
    assert "FileExists(AddBackslash(Value) + '.venv" in text
    assert "GetWorkspace" in text


def test_installer_profile_bundles_both_runtimes_without_msix_identity():
    root = ET.parse(APP / "Properties/PublishProfiles/Installer.pubxml").getroot()
    values = {node.tag: node.text for group in root for node in group}
    assert values["WindowsPackageType"] == "None"
    assert values["WindowsAppSDKSelfContained"] == "true"
    assert values["SelfContained"] == "true"
    assert values["PublishTrimmed"] == "false"
    assert values["RuntimeIdentifier"] == "win-x64"


def test_installer_version_and_changelog_match_app():
    root = ET.parse(APP / "NektronMoments.csproj").getroot()
    version = root.find("./PropertyGroup/Version").text
    assert f'#define AppVersion "{version}"' in (INSTALLER / "NektronMoments.iss").read_text()
    assert f"## [{version}] -" in (ROOT / "CHANGELOG.md").read_text()


def test_setup_ico_is_the_same_verified_moments_identity():
    assert hashlib.sha256((INSTALLER / "assets/NektronMoments.SetupIcon.ico").read_bytes()).digest() == (
        hashlib.sha256((APP / "Assets/AppIcon.ico").read_bytes()).digest()
    )
    generator = (INSTALLER / "Generate-InstallerBranding.ps1").read_text()
    assert "DrawImageUnscaled" in generator
    assert "NektronMoments_Complete_Brand_Package_v1_3" in generator


def test_release_requires_signatures_and_refuses_overwriting_versions():
    builder = (INSTALLER / "Build-InnoInstaller.ps1").read_text()
    signer = (INSTALLER / "Sign-Artifact.ps1").read_text()
    assert "completed installers are immutable" in builder
    assert "Assert-Signed $installer" in builder
    assert "Move-Item -LiteralPath $installer -Destination $final" in builder
    assert "SignedUninstaller=yes" in (INSTALLER / "NektronMoments.iss").read_text()
    assert "TimeStamperCertificate" in signer
    assert "Nektron, Inc." in signer
    assert "credentials.json" in builder


def test_unpacked_ui_does_not_directly_require_packaged_settings():
    for name in ("MainPage.xaml.cs", "MainWindow.xaml.cs"):
        assert "ApplicationData.Current" not in (APP / name).read_text()
    prefs = (APP / "Services/UserPreferences.cs").read_text()
    assert "preferences.json" in prefs
    assert "catch (Exception)" in prefs
    assert "InstalledWorkspace" in prefs
