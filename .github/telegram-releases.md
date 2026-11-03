# Release publishing

Published GitHub releases trigger the z3n.pro deploy hook and a Telegram
announcement using the release notes and download assets.

Repository secrets:
- `VERCEL_DEPLOY_HOOK`: the z3n.pro production hook.
- `TELEGRAM_BOT_TOKEN`: the same bot token used by Cronopus.

Production routing is configured with repository Actions variables:
- `TELEGRAM_MODE=prod`
- `TELEGRAM_PROD_CHAT_ID=-1002225536706`
- `TELEGRAM_PROD_TOPIC_ID=366`

Manual `notify-site` runs also rebuild the site. Set `release_tag` to select
a published release, `telegram_mode=prod` for topic 366, and `preview_only=true`
to prepare an artifact without sending a Telegram message.

Do not blindly rerun a send after a network failure: delivery may be unknown.
Run formatter tests with `rtk node --test .github/scripts/telegram-release.test.mjs`.
