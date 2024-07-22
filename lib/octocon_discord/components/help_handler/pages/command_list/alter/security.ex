defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Alter.Security do
  use OctoconDiscord.Components.HelpHandler.Pages

  def embeds do
    [
      %Embed{
        title: "#{Emojis.slashcommand()} `/alter security`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/alter security` command sets an alter's security level. Security levels determine who can view an alter's profile.
        ### Usage
        ```
        /alter security <id> <level>
        ```
        ### Parameters
        - `id`: The ID (or alias) of the alter whose security level to set. See the FAQ for more information about IDs and aliases.
        - `level`: The new security level. Must be one of the following:
          - `Private`: Only you can view the alter's profile. **This is the default.**
          - `Trusted friends only`: **Trusted** friends can also view the alter's profile.
          - `Friends only`: **All friends** can also view the alter's profile.
          - `Public`: Anyone can view the alter's profile.
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          back_button("alter_root", uid)
        ]
      }
    ]
  end
end
