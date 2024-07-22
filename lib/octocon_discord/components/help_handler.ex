defmodule OctoconDiscord.Components.HelpHandler do
  @moduledoc false
  use GenServer

  alias Nostrum.Api

  alias OctoconDiscord.{
    Utils
  }

  alias OctoconDiscord.Components.HelpHandler.Pages

  @table :help_command

  @pages %{
    root: Pages.Root,
    command_list: Pages.CommandList,
    help: Pages.CommandList.Help,
    register: Pages.CommandList.Register,
    settings_root: Pages.CommandList.Settings,
    settings_import_pk: Pages.CommandList.Settings.ImportPk,
    settings_import_sp: Pages.CommandList.Settings.ImportSp,
    settings_username: Pages.CommandList.Settings.Username,
    settings_avatar: Pages.CommandList.Settings.Avatar,
    settings_system_tag: Pages.CommandList.Settings.SystemTag,
    settings_show_system_tag: Pages.CommandList.Settings.ShowSystemTag,
    settings_proxy_case_sensitivity: Pages.CommandList.Settings.ProxyCaseSensitivity,
    settings_proxy_show_pronouns: Pages.CommandList.Settings.ProxyShowPronouns,
    settings_ids_as_aliases: Pages.CommandList.Settings.IdsAsAliases,
    alter_root: Pages.CommandList.Alter,
    alter_create: Pages.CommandList.Alter.Create,
    alter_view: Pages.CommandList.Alter.View,
    alter_edit: Pages.CommandList.Alter.Edit,
    alter_delete: Pages.CommandList.Alter.Delete,
    alter_list: Pages.CommandList.Alter.List,
    alter_security: Pages.CommandList.Alter.Security,
    alter_avatar: Pages.CommandList.Alter.Avatar,
    alter_proxy: Pages.CommandList.Alter.Proxy,
    alter_remove_alias: Pages.CommandList.Alter.RemoveAlias,
    front_root: Pages.CommandList.Front,
    front_add: Pages.CommandList.Front.Add,
    front_set: Pages.CommandList.Front.Set,
    front_end: Pages.CommandList.Front.End,
    front_view: Pages.CommandList.Front.View,
    front_primary: Pages.CommandList.Front.Primary,
    front_remove_primary: Pages.CommandList.Front.RemovePrimary,
    friend_root: Pages.CommandList.Friend,
    friend_list: Pages.CommandList.Friend.List,
    friend_list_requests: Pages.CommandList.Friend.ListRequests,
    friend_add: Pages.CommandList.Friend.Add,
    friend_remove: Pages.CommandList.Friend.Remove,
    friend_accept: Pages.CommandList.Friend.Accept,
    friend_reject: Pages.CommandList.Friend.Reject,
    friend_cancel: Pages.CommandList.Friend.Cancel,
    friend_trust: Pages.CommandList.Friend.Trust,
    friend_untrust: Pages.CommandList.Friend.Untrust,
    reproxy: Pages.CommandList.Reproxy,
    autoproxy: Pages.CommandList.Autoproxy,
    admin_root: Pages.CommandList.Admin,
    admin_channel_blacklist: Pages.CommandList.Admin.ChannelBlacklist,
    admin_view_settings: Pages.CommandList.Admin.ViewSettings,
    admin_force_system_tags: Pages.CommandList.Admin.ForceSystemTags,
    admin_log_channel: Pages.CommandList.Admin.LogChannel,
    danger_root: Pages.CommandList.Danger,
    danger_wipe_alters: Pages.CommandList.Danger.WipeAlters,
    danger_delete_account: Pages.CommandList.Danger.DeleteAccount,
    bot_info: Pages.CommandList.BotInfo,
    faq_root: Pages.Faq,
    faq_get_started: Pages.Faq.GetStarted,
    faq_import_alters: Pages.Faq.ImportAlters,
    faq_commands_private: Pages.Faq.CommandsPrivate,
    faq_invite_octocon: Pages.Faq.InviteOctocon,
    faq_who_can_see: Pages.Faq.WhoCanSee,
    faq_ids_aliases: Pages.Faq.IdsAliases,
    faq_autoproxy: Pages.Faq.Autoproxy,
    faq_delete_alters: Pages.Faq.DeleteAlters,
    faq_another_question: Pages.Faq.AnotherQuestion,
    resources: Pages.Resources
  }

  def pages, do: @pages

  def start_link([]) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  def handle_init do
    uid = :erlang.unique_integer([:positive])

    data = %{
      current_page: :root,
      uid: uid
    }

    :ets.insert(@table, {uid, data})

    # TODO: Possibly clean up correlations after a while?
    Process.send_after(__MODULE__, {:drop, uid}, :timer.minutes(10))

    generate_response(data)
    |> Keyword.put(:ephemeral?, true)
  end

  defp generate_response(%{current_page: current_page, uid: uid}) do
    page = Map.get(@pages, current_page)

    [
      embeds: page.embeds(),
      components: page.components(uid)
    ]
  end

  def handle_interaction("nav", uid, interaction) do
    page = interaction.data.values |> hd() |> String.to_existing_atom()

    old_data =
      :ets.lookup(@table, uid)
      |> hd()
      |> elem(1)

    data = %{
      old_data
      | current_page: page
    }

    :ets.insert(@table, {uid, data})

    Api.create_interaction_response(interaction, %{
      type: 7,
      data: generate_response(data) |> Enum.into(%{})
    })
  rescue
    _ -> create_expired_response(interaction)
  end

  def handle_interaction("nav-" <> page, uid, interaction) do
    page_atom = String.to_existing_atom(page)

    old_data =
      :ets.lookup(@table, uid)
      |> hd()
      |> elem(1)

    data = %{
      old_data
      | current_page: page_atom
    }

    :ets.insert(@table, {uid, data})

    Api.create_interaction_response(interaction, %{
      type: 7,
      data: generate_response(data) |> Enum.into(%{})
    })
  rescue
    _ -> create_expired_response(interaction)
  end

  defp create_expired_response(interaction) do
    Api.create_interaction_response(interaction, %{
      type: 7,
      data:
        Utils.error_embed("This help interface has expired. Please run `/help` again.")
        |> Enum.into(%{})
        |> Map.drop([:ephemeral?])
        |> Map.put(:components, nil)
    })
  end

  def drop(uid) do
    :ets.delete(@table, uid)
  end

  @impl true
  def init([]) do
    :ets.new(@table, [
      :set,
      :named_table,
      :public,
      read_concurrency: true,
      write_concurrency: :auto,
      decentralized_counters: true
    ])

    {:ok, %{}}
  end

  @impl true
  def handle_info({:drop, uid}, state) do
    :ets.delete(@table, uid)
    {:noreply, state}
  end
end
