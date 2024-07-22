defmodule OctoconDiscord.Components.HelpHandler.Pages.CommandList.Settings do
  use OctoconDiscord.Components.HelpHandler.Pages

  @commands [
    %{
      name: "import-pk",
      description: "Imports data from PluralKit",
      nav_page: "settings_import_pk"
    },
    %{
      name: "import-sp",
      description: "Imports data from Simply Plural",
      nav_page: "settings_import_sp"
    },
    %{
      name: "username",
      description: "Changes your system's username",
      nav_page: "settings_username"
    },
    %{
      name: "avatar",
      description: "Manages your system-wide avatar",
      nav_page: "settings_avatar"
    },
    %{
      name: "system-tag",
      description: "Changes your system tag",
      nav_page: "settings_system_tag"
    },
    %{
      name: "show-system-tag",
      description: "Sets whether your system tag tag is shown",
      nav_page: "settings_show_system_tag"
    },
    %{
      name: "proxy-case-sensitivity",
      description: "Sets whether proxying is case-insensitive",
      nav_page: "settings_proxy_case_sensitivity"
    },
    %{
      name: "proxy-show-pronouns",
      description: "Sets whether pronouns show in proxies",
      nav_page: "settings_proxy_show_pronouns"
    },
    %{
      name: "ids-as-aliases",
      description: "Toggles whether alter IDs/aliases can be used as automatic proxy prefixes",
      nav_page: "settings_ids_as_aliases"
    }
  ]

  def embeds do
    [
      %Embed{
        title: "#{Emojis.folder()} `/settings`",
        color: Utils.hex_to_int("#0FBEAA"),
        description: """
        The `/settings` command group configures various settings for your system, as well as import data from other platforms. Choose a subcommand below to learn more about it!
        """
      }
    ]
  end

  def components(uid) do
    [
      %{
        type: 1,
        components: [
          %{
            type: 3,
            custom_id: "help|nav|#{uid}",
            options:
              @commands
              |> Enum.map(fn %{name: name, description: description, nav_page: nav_page} ->
                %{
                  label: "/" <> name,
                  value: nav_page,
                  description: description,
                  emoji: map_emoji(Emojis.slashcommand())
                }
              end)
          }
        ]
      },
      %{
        type: 1,
        components: [
          back_button("command_list", uid)
        ]
      }
    ]
  end
end
