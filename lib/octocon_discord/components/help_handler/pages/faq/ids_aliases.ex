defmodule OctoconDiscord.Components.HelpHandler.Pages.Faq.IdsAliases do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.faq()} How do IDs and aliases work?",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        In Octocon, every alter is identified by a numerical ID. Notably, this ID is unique on a by-system basis, but not globally. This means that two different users `Atlas` and `Prometheus` can *both* have an alter with the ID `1`.

        IDs are used to reference alters in commands and other contexts. For example, the `/alter edit` command accepts an `id` parameter to specify which alter to edit.
        ### Aliases
        Aliases are a way to give your alters a more human-readable ID. You can set an alias for an alter using the `/alter edit` command with the `alias` parameter. For example, `/alter edit id:1 alias:Gaia` would set the alias of the alter with ID `1` to `Gaia`.

        Once an alter has an alias, you can use it in place of their numerical ID in all commands. In the above example, `/alter view id:Gaia` would view the alter with ID `1`.

        If you'd like to remove an alter's alias, you can do so with the `/alter remove-alias` command.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("faq_root", uid)
        ]
      }
    ]
  end
end
