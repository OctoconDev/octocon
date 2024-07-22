defmodule Octocon.Workers.SimplyPluralImportWorker do
  @moduledoc """
  An Oban worker that imports alters from Simply Plural into a system with the given ID.

  ## Arguments

  - `system_id` (binary): The ID of the system to import alters into.
  - `sp_token` (binary): The Simply Plural API token to use for the request.
  """

  alias Octocon.Accounts
  alias Octocon.Alters
  alias Octocon.Repo
  alias Octocon.Alters.Alter

  require Logger

  use Oban.Worker,
    queue: :sp_imports,
    unique: [
      fields: [:args, :worker],
      keys: [:system_id],
      states: [:available, :scheduled, :executing, :retryable],
      period: :infinity
    ],
    max_attempts: 3

  alias OctoconWeb.Uploaders.Avatar

  @sp_endpoint URI.parse("https://api.apparyllis.com/v1/")
  @cdn_endpoint URI.parse("https://spaces.apparyllis.com/")

  @impl true
  def perform(%Oban.Job{args: %{"system_id" => system_id, "sp_token" => sp_token}}) do
    try do
      %{
        "id" => id,
        "content" => %{
          "desc" => description
        }
      } = get_system_data(sp_token)

      {:ok, %{body: body}} = send_sp_request(:get, "/members/#{id}", sp_token)

      start_count = Accounts.get_user!({:system, system_id}).lifetime_alter_count + 1

      {alters, avatars} =
        Jason.decode!(body)
        |> Stream.map(& &1["content"])
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
            description: default_if_empty(description, 3000, user.description)
          }
        )
      end)

      OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "alters_created", %{
        alters: Enum.map(alters, &OctoconWeb.System.AlterJSON.data_me(&1))
      })

      OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "sp_import_complete", %{
        alter_count: alter_count
      })

      OctoconDiscord.Utils.send_dm(
        {:system, system_id},
        "Import complete (Simply Plural)",
        "#{alter_count} alters have been successfully imported from Simply Plural. They have been assigned IDs #{start_count} - #{start_count + alter_count - 1}.\n\n**Note:** This process should only be completed once; doing it again will result in duplicate alters."
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
      #       # Avatar doesn't exist; stale reference on SP's end?
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
              # Avatar doesn't exist; stale reference on SP's end?
              :ok
          end
        end,
        # NOTE: Potentially replace with schedulers_online on a beefier server?
        max_concurrency: 8,
        ordered: false,
        timeout: :timer.seconds(10),
        on_timeout: :kill_task
      )
      |> Stream.run()

      :ok
    rescue
      e ->
        reraise e, __STACKTRACE__
    end
  end

  defp get_system_data(token) do
    {:ok, %{body: body}} = send_sp_request(:get, "/me", token)
    Jason.decode!(body)
  end

  defp send_sp_request(method, endpoint, token) do
    uri = URI.append_path(@sp_endpoint, endpoint)

    res =
      Finch.build(method, uri, [
        {"User-Agent", "Octocon/spimport; contact = contact@octocon.app"},
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
        pronouns: default_if_empty(alter["pronouns"], 50),
        description: default_if_empty(alter["desc"], 2000),
        color: parse_color(alter["color"]),
        alias: nil,
        fields: [],
        inserted_at: NaiveDateTime.utc_now(:second),
        updated_at: NaiveDateTime.utc_now(:second)
      },
      if alter["avatarUuid"] != nil and String.length(alter["avatarUuid"]) != 0 do
        random_id = Nanoid.generate(30)

        avatar_url =
          @cdn_endpoint
          |> URI.merge("/avatars/#{alter["uid"]}/#{alter["avatarUuid"]}")
          |> URI.to_string()

        {
          avatar_url,
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

  defp parse_color("#" <> _ = color) when byte_size(color) == 7, do: color
  defp parse_color(color) when byte_size(color) == 6, do: color
  defp parse_color(color) when color == "", do: nil
  defp parse_color(nil), do: nil
end
