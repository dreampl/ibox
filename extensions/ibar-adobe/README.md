# IBAR Bridge for Adobe apps (CEP) — experimental

An invisible CEP extension that tells IBAR when After Effects / Premiere render (render queue state) and when
Photoshop finishes a save or export. Only state and a short title go to IBAR, through IBAR's own `ibar-host.exe`.

**Status: built, not run.** On the test PC Photoshop's automation stayed busy and Adobe's unsigned-extension switch
was off, so this panel was never loaded in a real Adobe app.

Install (per user, unsigned):

1. Copy the `ibar-adobe` folder to `%APPDATA%\Adobe\CEP\extensions\ibar-adobe`.
2. Unsigned extensions load only with Adobe's debug switch, which is your decision:
   `HKCU\Software\Adobe\CSXS.12`, string value `PlayerDebugMode` = `1` (CSXS.11 for older apps).
3. Restart the Adobe app.

Limits: After Effects does not give scripts a render percentage, so the circle turns (unknown amount) while a comp
renders and ends when it is done or failed. Illustrator offers no save / export events to extensions.
