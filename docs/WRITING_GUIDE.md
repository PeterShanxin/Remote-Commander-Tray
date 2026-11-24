# Writing guide

This guide applies to human and agent contributions: documentation, README files, help, errors, UI labels, source comments and delivery notes. It is inspired by Simplified Technical English. Apply it to new or changed text; do not rewrite the repository to enforce style.

- Use one term for each concept. Distinguish the tray app from the official Remote Device and the Store edition from the portable edition.
- Name the actor, action and object. Put the condition before the action it controls.
- Separate actions when the reader needs to perform them in order. Preserve technical meaning and important warnings.
- Use the reader's language. Keep Chinese natural rather than applying English word-count rules. Match an existing UI label exactly when telling someone what to click.
- State the check, its result and any remaining limitation. A build is not proof of a successful installation or sign-in.

**Before:** “Restart it and make sure things work.”

**After:** “Select **Restart connection** in the tray menu. Confirm that the tray shows **Online**.”

**中文：** 在托盘菜单中选择 **Restart connection**，确认托盘显示 **Online**。

Do not add prose lint or dependencies to enforce these guidelines. Review the meaning in context.
