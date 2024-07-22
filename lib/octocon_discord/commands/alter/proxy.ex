defmodule OctoconDiscord.Commands.Alter.Proxy do
  @moduledoc false

  require Logger

  alias OctoconDiscord.ProxyCache

  alias Octocon.Alters

  alias OctoconDiscord.{
    ProxyCache,
    Utils
  }

  import OctoconDiscord.Utils, only: [with_id_or_alias: 2]

  @subcommands %{
    "set" => &__MODULE__.set/2,
    "add" => &__MODULE__.add/2,
    "remove" => &__MODULE__.remove/2,
    "clear" => &__MODULE__.clear/2
  }

  def command(%{system_identity: system_identity} = context, options) do
    subcommand = hd(options)

    with_id_or_alias(subcommand.options, fn alter_identity ->
      alter_id = Alters.resolve_alter(system_identity, alter_identity)

      if alter_id != false do
        @subcommands[subcommand.name].(
          context
          |> Map.put(:alter_identity, {:id, alter_id}),
          subcommand.options
        )
      else
        case alter_identity do
          {:id, alter_id} ->
            Utils.error_embed("You don't have an alter with ID **#{alter_id}**.")

          {:alias, aliaz} ->
            Utils.error_embed("You don't have an alter with the alias **#{aliaz}**.")
        end
      end
    end)
  end

  def remove(
        %{
          system_identity: system_identity,
          alter_identity: alter_identity,
          discord_id: discord_id
        } = context,
        options
      ) do
    prefix = Utils.get_command_option(options, "prefix") || ""
    suffix = Utils.get_command_option(options, "suffix") || ""

    if String.length(prefix) == 0 && String.length(suffix) == 0 do
      Utils.error_embed("You must provide a prefix or suffix for your proxy.")
    else
      proxy = prefix <> "text" <> suffix

      proxies =
        Alters.get_alter_by_id!(system_identity, alter_identity, [:discord_proxies])
        |> Map.get(:discord_proxies, [])

      if Enum.member?(proxies, proxy) do
        ProxyCache.evict_proxies(discord_id)

        OctoconDiscord.Commands.Alter.update_alter(
          context,
          alter_identity,
          %{discord_proxies: Enum.reject(proxies, fn p -> p == proxy end)},
          "Successfully removed proxy `#{proxy}` from alter!",
          true
        )
      else
        Utils.error_embed("That alter doesn't have a proxy with that prefix and suffix.")
      end
    end
  end

  def add(
        %{
          system_identity: system_identity,
          alter_identity: alter_identity,
          discord_id: discord_id
        } = context,
        options
      ) do
    validate_proxy(context, options, fn proxy ->
      existing_proxies =
        Alters.get_alter_by_id!(system_identity, alter_identity, [:discord_proxies])
        |> Map.get(:discord_proxies, [])

      ProxyCache.evict_proxies(discord_id)

      OctoconDiscord.Commands.Alter.update_alter(
        context,
        alter_identity,
        %{
          discord_proxies: [proxy | existing_proxies]
        },
        "Successfully added proxy `#{proxy}` to alter!",
        true
      )
    end)
  end

  def set(%{alter_identity: alter_identity, discord_id: discord_id} = context, options) do
    validate_proxy(context, options, fn proxy ->
      ProxyCache.evict_proxies(discord_id)

      OctoconDiscord.Commands.Alter.update_alter(
        context,
        alter_identity,
        %{discord_proxies: [proxy]},
        "Successfully set proxy to `#{proxy}` for alter!",
        true
      )
    end)
  end

  def clear(%{alter_identity: alter_identity, discord_id: discord_id} = context, _options) do
    ProxyCache.evict_proxies(to_string(discord_id))

    OctoconDiscord.Commands.Alter.update_alter(
      context,
      alter_identity,
      %{discord_proxies: []},
      "Successfully removed all proxies from alter!",
      true
    )
  end

  defp validate_proxy(%{discord_id: discord_id}, options, callback) do
    prefix = Utils.get_command_option(options, "prefix") || ""
    suffix = Utils.get_command_option(options, "suffix") || ""

    if String.length(prefix) == 0 && String.length(suffix) == 0 do
      Utils.error_embed("You must provide a prefix or suffix for your proxy.")
    else
      proxy_exists? =
        ProxyCache.get(discord_id)
        |> elem(1)
        |> Map.get(:proxies, [])
        |> Enum.any?(fn {{pre, suf, _}, _} ->
          prefix == pre && suffix == suf
        end)

      if proxy_exists? do
        Utils.error_embed("One of your alters already has a proxy with that prefix and suffix.")
      else
        callback.(prefix <> "text" <> suffix)
      end
    end
  end
end
