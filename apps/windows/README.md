# Nektron Moments for Windows

Windows is a first-class native client alongside iOS and Android. The current
architecture plan is a packaged C# WinUI 3 application using the shared
media/source/upload API and its own durable local library cache.

The [brand package](../../Brand_Images/README.md) contains references from
Nektron Write, Nektron Mail, and InterviewHelperAI. Their current code uses WPF.
Their visual language can inform WinUI; WPF controls are not directly reusable
WinUI components. See the [source review](../../Brand_Images/SOURCE-REVIEW.md).

The desktop experience includes folder import/watching, a virtualized media
library, photo/video detail, search, source modes, and quiet background activity.
Plan keyboard navigation, selection, per-monitor DPI, window resizing, and both
themes from the first screen. Final Moments-specific artwork and layouts are
being developed by the aesthetics model.

This folder currently records the client boundary; a Windows app project has
not been scaffolded yet.
