# PoSoccer — Play Console internal testing submission

Read from the live PoSumo listing on 2026-08-03 (personal account
`7241994012654371286`, Matthew Herb). PoSumo is app `4976464532402323504`,
package `com.punkoutersoftware.posumo`, still **Draft** on internal testing,
last updated Jul 31 2026.

## PoSumo's answers, to mirror

| Declaration | PoSumo's answer | Applies to PoSoccer? |
|---|---|---|
| Data safety | App doesn't collect or share data. Data isn't encrypted. | Yes — PoSoccer has no networking stack and no analytics SDK |
| Target audience and content | Target age group: **18 and over** | Yes — keeps you out of the Families policy programme |
| Content ratings | Everyone / ESRB E / PEGI 3 / USK 0 / IARC 3+ | Yes — 2D sports game, no violence, no user interaction |
| Ads | App doesn't contain ads | Yes |
| Sign in details | All functionality is available without special access | Yes — no login, no gated content |
| Privacy policy | `https://app-popunkoutersoftware.azurewebsites.net//privacy` | **Broken — see below** |
| Health apps | Actioned | No health features |
| Financial features | Actioned | No financial features |
| Government apps | Actioned | Not a government app |
| **Advertising ID** | **NOT COMPLETED** | **Must be done — see below** |

Content ratings can't literally be copied: the rating comes from IARC, so the
questionnaire has to be filled in fresh per app. Answering it honestly for a
2D soccer game with no violence, no gambling, no user-to-user communication
and no user-generated content produces the same Everyone / PEGI 3 result.

## Three problems found

### 1. The privacy policy URL is dead

Opened from the Play Console listing in the browser, it returns:

```json
{"title":"An error occurred while processing the request.","status":409,
 "detail":"An internal error occurred. See server logs for details.",
 "instance":"/privacy"}
```

Not a fetch-tool artifact — this reproduces in the browser. The Azure app is
throwing a 409 server-side. Two things to fix:

- The **service itself is erroring.** A privacy policy URL is mandatory for
  every app, and reviewers do open it. This will fail review for PoSoccer, and
  it is presumably already a problem for PoSumo.
- The URL has a **double slash**: `azurewebsites.net//privacy`. Even once the
  service is healthy, tidy that to a single slash.

Cheapest fix if the Azure app is not worth reviving: host a static privacy
policy anywhere public — a GitHub Pages file or a gist rendered page is
completely acceptable to Google for a no-data-collection app.

### 2. PoSumo never completed the Advertising ID declaration

It is the single item under "Need attention", and its own help text explains
the consequence:

> You will not be able to submit releases targeting Android 13 until you
> complete this section.

That is almost certainly why PoSumo is stuck — its dashboard shows countries,
testers and a created release all ticked, but "Preview and confirm the release"
and "Send the release to Google for review" unticked. PoSoccer targets API 36,
well past 13, so it will hit exactly the same wall.

For PoSoccer the answer is **No, this app does not use an advertising ID** —
there is no ads SDK, no Firebase, no Unity Analytics in the package list. Worth
completing on PoSumo too while you're in there.

### 3. Package name — RESOLVED 2026-08-03

Was `com.posoccer.app`, which did not match PoSumo's `com.punkoutersoftware.*`
convention. Changed in Unity and verified in `ProjectSettings.asset`:

```
applicationIdentifier:
  Android: com.punkoutersoftware.posoccer
```

| App | Package |
|---|---|
| PoSumo | `com.punkoutersoftware.posumo` |
| PoSoccer | `com.punkoutersoftware.posoccer` |

This value is **permanent** once the app is created in Play — the only remedy
for a wrong package name is an entirely new listing. It is correct now; do not
change it again after the first upload.

`companyName` was also changed from `PoSoccer` to `punkoutersoftware` at the
same time. Note this does **not** feed the package name — Unity only derives a
bundle id from `companyName`/`productName` when one has not been set
explicitly, and ours is set explicitly. Verified after the change that
`applicationIdentifier.Android` was unaffected.

One cosmetic leftover, not user-visible on Play (the developer name on the
store comes from the Play account, not from Unity):
`applicationIdentifier.Standalone` is still `com.DefaultCompany.2D-URP`, the
original template default. It has no bearing on the Android build.

## Order of operations

1. Fix the privacy policy URL (or host a static one) and confirm it loads.
2. Decide the package name; if changing, set it in Unity first.
3. Generate the upload keystore, set the four `POSOCCER_*` env vars.
4. `.\scripts\build-android-aab.ps1 -VersionName 0.1.0 -VersionCode 1`
5. In Play Console: create the app, complete App content using the table above
   — **including Advertising ID** — then Internal testing → create release,
   upload the `.aab`, add testers.

Steps 1–4 are yours or mine. Step 5's declarations are legal statements you
make as the developer; I can fill the forms with the answers above once you
tell me to, but you should be the one to submit them.
