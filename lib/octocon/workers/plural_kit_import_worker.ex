defmodule Octocon.Workers.PluralKitImportWorker do
  @moduledoc """
  An Oban worker that imports alters from PluralKit into a system with the given ID.

  ## Arguments

  - `system_id` (binary): The ID of the system to import alters into.
  - `pk_token` (binary): The PluralKit API token to use for the request.
  """

  alias Octocon.Alters
  alias Octocon.Alters.Alter
  alias Octocon.Accounts
  alias Octocon.Repo

  use Oban.Worker,
    queue: :pk_imports,
    unique: [
      fields: [:args, :worker],
      keys: [:system_id],
      states: [:available, :scheduled, :executing, :retryable],
      period: :infinity
    ],
    max_attempts: 3

  require Logger

  alias OctoconWeb.Uploaders.Avatar

  @pk_endpoint URI.parse("https://api.pluralkit.me/v2/")

  @impl true
  def perform(%Oban.Job{
        args: %{
          "system_id" => system_id,
          "pk_token" => pk_token
        }
      }) do
    {:ok, %{body: self_body}} = send_pk_request(:get, "/systems/@me", pk_token)
    {:ok, %{body: alters_body}} = send_pk_request(:get, "/systems/@me/members", pk_token)

    start_count = Accounts.get_user!({:system, system_id}).lifetime_alter_count + 1

    %{
      "description" => description,
      "tag" => system_tag
    } = Jason.decode!(self_body)

    {alters, avatars} =
      Jason.decode!(alters_body)
      |> Stream.with_index(start_count)
      |> Stream.map(fn {alter, index} ->
        parse_alter(system_id, alter, index)
      end)
      |> Enum.reduce({[], []}, fn
        {alter, nil}, {alters, avatars} ->
          {[alter | alters], avatars}

        {alter, avatar}, {alters, avatars} ->
          {[alter | alters], [avatar | avatars]}
      end)

    alter_count = length(alters)

    Logger.info("Got alters: #{alter_count}")

    chunked_alters =
      alters
      |> Stream.map(
        &Map.drop(&1, [:__meta__, :__struct__, :fronts, :user, :global_journals, :tags])
      )
      |> Enum.chunk_every(1000)

    Repo.transaction(fn ->
      chunked_alters
      |> Enum.each(fn chunk ->
        Repo.insert_all(Alter, chunk)
      end)

      user = Accounts.get_user!({:system, system_id})

      Accounts.update_user(
        user,
        %{
          lifetime_alter_count: user.lifetime_alter_count + alter_count,
          description: default_if_empty(description, 3000, user.description),
          system_tag: default_if_empty(system_tag, 20, user.system_tag)
        }
      )
    end)

    OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "alters_created", %{
      alters: Enum.map(alters, &OctoconWeb.System.AlterJSON.data_me(&1))
    })

    OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "pk_import_complete", %{
      alter_count: alter_count
    })

    OctoconDiscord.Utils.send_dm(
      {:system, system_id},
      "Import complete (PluralKit)",
      "#{alter_count} alters have been successfully imported from PluralKit. They have been assigned IDs #{start_count} - #{start_count + alter_count - 1}. It may take a while longer for their avatars to be processed.\n\n**Note:** This process should only be completed once; doing it again will result in duplicate alters."
    )

    OctoconDiscord.ProxyCache.invalidate({:system, system_id})

    # Enum.each(avatars, fn {avatar_url, avatar_scope} ->
    #   case Avatar.store({avatar_url, avatar_scope}) do
    #     {:ok, _} ->
    #       octo_url = Avatar.url({"primary.jpg", avatar_scope}, :primary)

    #       Alters.update_alter(
    #         avatar_scope.system_id,
    #         avatar_scope.alter_id,
    #         %{avatar_url: octo_url}
    #       )

    #     _ ->
    #       # Avatar doesn't exist; stale reference on PK's end?
    #       :ok
    #   end
    # end)

    Task.async_stream(
      avatars,
      fn {avatar_url, avatar_scope} ->
        case Avatar.store({avatar_url, avatar_scope}) do
          {:ok, _} ->
            octo_url = Avatar.url({"primary.webp", avatar_scope}, :primary)

            Alters.update_alter(
              {:system, avatar_scope.system_id},
              {:id, avatar_scope.alter_id},
              %{avatar_url: octo_url}
            )

          _ ->
            # Avatar doesn't exist; stale reference on PK's end?
            :ok
        end
      end,
      # NOTE: Potentially replace with schedulers_online on a beefier server?
      max_concurrency: 4,
      ordered: false,
      timeout: :timer.seconds(10),
      on_timeout: :kill_task
    )
    |> Stream.run()

    :ok
  end

  defp send_pk_request(method, endpoint, token) do
    uri = URI.append_path(@pk_endpoint, endpoint)

    res =
      Finch.build(method, uri, [
        {"Content-Type", "application/json"},
        {"User-Agent", "Octocon/pkimport; contact = contact@octocon.app"},
        {"Authorization", token}
      ])
      |> Finch.request(Octocon.Finch)

    res
  end

  defp parse_alter(system_id, alter, id) do
    {
      %Alter{
        user_id: system_id,
        id: id,
        name: default_if_empty(alter["name"], 80, "Unnamed alter"),
        proxy_name: default_if_empty(alter["display_name"], 80),
        discord_proxies:
          if(alter["proxy_tags"],
            do:
              alter["proxy_tags"]
              |> Enum.map(fn tag -> (tag["prefix"] || "") <> "text" <> (tag["suffix"] || "") end),
            else: nil
          ),
        pronouns: default_if_empty(alter["pronouns"], 50),
        description: default_if_empty(alter["description"], 2000),
        alias: nil,
        color: parse_color(alter["color"]),
        fields: [],
        inserted_at: NaiveDateTime.utc_now(:second),
        updated_at: NaiveDateTime.utc_now(:second)
      },
      if alter["avatar_url"] do
        random_id = Nanoid.generate(30)

        {
          alter["avatar_url"],
          %{
            system_id: system_id,
            alter_id: id,
            random_id: random_id
          }
        }
      else
        nil
      end
    }
  end

  defp default_if_empty(string, max, default \\ nil)

  defp default_if_empty(nil, _max, default), do: default
  defp default_if_empty(string, _max, default) when string == "", do: default
  defp default_if_empty(string, max, _default), do: string |> String.slice(0..max)

  defp parse_color(nil), do: nil
  defp parse_color(color), do: "#" <> color
end
