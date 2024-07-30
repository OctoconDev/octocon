defmodule OctoconDiscord.Utils do
  @moduledoc """
  Various utility functions for Octocon's Discord frontend.
  """

  require Logger

  alias Nostrum.Api
  alias Nostrum.Cache.GuildCache
  alias Octocon.Accounts
  alias Octocon.Accounts.User

  @separator_field %Nostrum.Struct.Embed.Field{
    name: "\u200B",
    value: "\u200B",
    inline: false
  }

  @alias_regex ~r/^(?![\s\d])[^\n]{1,80}$/

  def get_cached_guild(id), do: wrap_cache_call(id, &GuildCache.get/1, &Api.get_guild!/1)

  defp wrap_cache_call(id, cache_function, api_function) do
    case cache_function.(id) do
      {:ok, data} ->
        # Logger.debug("Discord cache hit")
        data

      {:error, _} ->
        # Logger.debug("Discord cache miss")
        api_function.(id)
    end
  end

  def get_command_option(options, name) do
    case Enum.find(options, fn %{name: option} -> option == name end) do
      nil -> nil
      option -> Map.get(option, :value)
    end
  end

  def parse_id!(id) when is_integer(id), do: id
  def parse_id!(id) when is_binary(id), do: Integer.parse(id) |> elem(0)

  def alter_id_valid?(id) when is_integer(id) and id > 0 and id < 32_768, do: true

  def alter_id_valid?(id) when is_binary(id) do
    case Integer.parse(id) do
      {int, ""} when int > 0 and int < 32_768 -> true
      _ -> false
    end
  end

  def alter_id_valid?(_), do: false

  def alter_alias_valid?(aliaz), do: String.match?(aliaz, @alias_regex)

  def validate_alter_id(alter_id, callback) do
    if alter_id_valid?(alter_id) do
      callback.()
    else
      error_embed("You don't have an alter with ID **#{alter_id}**.")
    end
  end

  def validate_alias(aliaz) do
    if String.match?(aliaz, @alias_regex) do
      {:alias, aliaz}
    else
      {:error,
       "Invalid alias. An alias must meet the following criteria:\n\n- Between 1-80 characters\n- Cannot consist of just a number\n-Cannot start with a space"}
    end
  end

  def parse_idalias(idalias, allow_nil \\ false) do
    if idalias == nil or String.trim(idalias) == "" do
      if allow_nil do
        nil
      else
        {:error, "You must provide an alter ID (or alias) to use this command."}
      end
    else
      cond do
        alter_id_valid?(idalias) -> {:id, parse_id!(idalias)}
        true -> validate_alias(idalias)
      end
    end
  end

  def with_id_or_alias(options_or_idalias, callback, allow_nil \\ false) 

  def with_id_or_alias(options, callback, allow_nil) when is_list(options) do
    idalias = get_command_option(options, "id")

    with_id_or_alias(to_string(idalias), callback, allow_nil)
  end

  def with_id_or_alias(idalias, callback, allow_nil) when is_binary(idalias) do
    case parse_idalias(idalias, allow_nil) do
      {:error, error} -> error_embed(error)
      result -> callback.(result)
    end
  end

  def register_message,
    do:
      error_embed(
        "You're not registered. Use the `/register` command or link your Discord account to your existing system."
      )

  def ensure_registered(discord_id, callback) do
    if Accounts.user_exists?({:discord, discord_id}) do
      callback.()
    else
      register_message()
    end
  end

  def get_avatar_url(discord_id, avatar_hash),
    do: "https://cdn.discordapp.com/avatars/#{discord_id}/#{avatar_hash}.png"

  def hex_to_int(nil), do: hex_to_int("#0FBEAA")

  def hex_to_int(hex) do
    hex
    |> String.downcase()
    |> String.replace_leading("#", "")
    |> String.to_integer(16)
  end

  def validate_hex_color(color) do
    if String.match?(color, ~r/^#?[0-9A-Fa-f]{6}$/i) do
      {:ok, String.replace_leading(color, "#", "") |> String.upcase()}
    else
      :error
    end
  end

  def error_embed(error, ephemeral? \\ true),
    do: [
      embeds: [
        %{
          title: ":x: Whoops!",
          description: error,
          color: 0xFF0000
        }
      ],
      ephemeral?: ephemeral?
    ]

  def success_embed_raw(success) do
    %{
      title: ":white_check_mark: Success!",
      description: success,
      color: 0x00FF00
    }
  end

  def success_embed(success, ephemeral? \\ true),
    do: [
      embeds: [success_embed_raw(success)],
      ephemeral?: ephemeral?
    ]

  def system_embed_raw(system, self?) do
    %{
      title: "System information",
      description: system.description || nil,
      thumbnail: %{
        url: system.avatar_url || nil
      },
      fields:
        [
          %Nostrum.Struct.Embed.Field{
            name: "ID",
            value: system.id,
            inline: true
          },
          %Nostrum.Struct.Embed.Field{
            name: "Username",
            value: system.username || "*None*",
            inline: true
          },
          %Nostrum.Struct.Embed.Field{
            name: "Discord",
            value: if(system.discord_id, do: "<@#{system.discord_id}>", else: "Not linked"),
            inline: true
          }
        ] ++
          if(self?,
            do: [
              @separator_field,
              %Nostrum.Struct.Embed.Field{
                name: "Email linked",
                value: if(system.email, do: "Yes", else: "No"),
                inline: true
              },
              %Nostrum.Struct.Embed.Field{
                name: "System tag",
                value: system.system_tag || "*None*",
                inline: true
              }
            ],
            else: []
          ),
      footer: %Nostrum.Struct.Embed.Footer{
        text:
          if(self? and system.username == nil,
            do: "Tip: register a username with `/settings username <username>.`",
            else: nil
          )
      }
    }
  end

  def system_embed(system, self?) do
    [
      embeds: [system_embed_raw(system, self?)],
      ephemeral?: true
    ]
  end

  def alter_embed(alter, _guarded \\ false) do
    normalized_description =
      (alter.description || "")
      |> String.replace("\\n", "\n")
      |> String.trim()

    description =
      case String.length(normalized_description) do
        0 ->
          "*No description*"

        length when length > 1500 ->
          normalized_description
          |> String.slice(0..1500)
          |> Kernel.<>("\n...")

        _ ->
          normalized_description
      end

    # TODO: Fields
    %{
      title: alter.name,
      description: description,
      color: hex_to_int(alter.color),
      fields: [
        %{
          name: "Pronouns",
          value: alter.pronouns || "None",
          inline: true
        },
        %{
          name: "Proxies",
          inline: true,
          value:
            case alter.discord_proxies do
              [] ->
                "None"

              proxies ->
                proxies
                |> Enum.map_join("\n", fn proxy ->
                  "- `#{proxy}`"
                end)
            end
        },
        %{
          name: "ID",
          value: alter.id,
          inline: true
        },
        %{
          name: "Alias",
          value: alter.alias || "None",
          inline: true
        },
        %{
          name: "Proxy name",
          value: alter.proxy_name || "None",
          inline: true
        },
        %{
          name: "Color",
          value: alter.color || "None",
          inline: true
        }
        # %{
        #    name: "Description",
        #    value: case alter.description do
        #      "" -> "None"
        #      nil -> "None"
        #      description ->
        #       description
        #       |> String.replace("\\n", "\n")
        #    end
        # }
      ],
      thumbnail: %{
        url: alter.avatar_url
      }
    }
  end

  def send_dm(%User{} = user, title, message) do
    spawn(fn ->
      unless user.discord_id == nil do
        channel = Api.create_dm!(Integer.parse(user.discord_id) |> elem(0))

        Api.create_message(channel.id, %{
          embeds: [
            %Nostrum.Struct.Embed{
              title: title,
              color: hex_to_int("#0FBEAA"),
              description: message
            }
          ]
        })
      end
    end)
  end

  def send_dm(system_identity, title, message) do
    user = Accounts.get_user!(system_identity)
    send_dm(user, title, message)
  end

  def send_dm(%User{} = user, options) do
    spawn(fn ->
      unless user.discord_id == nil do
        channel = Api.create_dm!(Integer.parse(user.discord_id) |> elem(0))

        Api.create_message(channel.id, options)
      end
    end)
  end

  def send_dm(system_identity, options) when is_map(options) do
    user = Accounts.get_user!(system_identity)
    send_dm(user, options)
  end

  def system_id_from_opts(opts, callback) do
    num_nil = Map.values(opts) |> Enum.count(&is_nil/1)

    cond do
      num_nil == 3 ->
        error_embed("You must specify a system ID, Discord ping, or username.")

      num_nil != 2 ->
        error_embed("You must *only* specify a system ID, Discord ping, *or* username.")

      opts.system_id ->
        if Accounts.user_exists?({:system, opts.system_id}) do
          callback.({:system, opts.system_id}, "**#{opts.system_id}**")
        else
          error_embed("A system does not exist with ID **#{opts.system_id}**.")
        end

      opts.discord_id ->
        discord_id = to_string(opts.discord_id)

        if Accounts.user_exists?({:discord, discord_id}) do
          callback.({:discord, discord_id}, "<@#{discord_id}>")
        else
          error_embed("A system does not exist with that Discord account.")
        end

      opts.username ->
        case Accounts.get_user_id_by_username(opts.username) do
          nil ->
            error_embed("A system does not exist with username **#{opts.username}**.")

          system_id ->
            callback.({:system, system_id}, "**#{opts.username}**")
        end

      true ->
        error_embed("An unknown error occurred.")
    end
  end

  def add_show_option(options) do
    options ++ [
      %{
        name: "show",
        description: "Show this message to the entire channel instead of just you.",
        type: :boolean,
        required: false
      }
    ] 
  end

  def get_show_option(options) do
    case get_command_option(options, "show") do
      nil -> false
      value -> value
    end
  end

  def get_guild_data do
    Nostrum.Cache.GuildCache.fold([], fn guild, acc ->
      [
        %{
          name: guild.name,
          member_count: guild.member_count,
          id: guild.id
        }
        | acc
      ]
    end)
    |> Enum.sort_by(& &1.member_count, :desc)
  end
end
