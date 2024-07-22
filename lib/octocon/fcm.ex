defmodule Octocon.FCM do
  @moduledoc """
  Utility module for sending push notifications to users using `Pigeon.Dispatcher`.
  """
  use Pigeon.Dispatcher, otp_app: :octocon

  require Logger

  import Ecto.Query, warn: false

  alias Octocon.Accounts
  alias Octocon.Alters
  alias Octocon.Alters.Alter
  alias Octocon.Friendships.Friendship
  alias Octocon.Repo
  alias Octocon.NotificationTokens
  alias Octocon.NotificationTokens.NotificationToken

  @size_limit 500

  @doc """
  Pushes a notification to a user's friends, notifying them of the alter front statuses that have been modified.
  """
  def push_friends_alters(user_id, alter_ids) do
    {title, image, data} = gen_batch_notifications(user_id, alter_ids)

    merger =
      if image == nil do
        %{}
      else
        %{"image" => image}
      end

    data
    |> Enum.flat_map(fn {tokens, {friend_id, message}} ->
      Enum.map(tokens, fn token ->
        {
          Pigeon.FCM.Notification.new(
            {:token, token},
            %{
              "title" => title,
              "body" => message
            }
            |> Map.merge(merger)
          ),
          gen_callback(friend_id)
        }
      end)
    end)
    |> Enum.each(fn {notification, callback} ->
      push(notification, on_response: callback)
    end)
  end

  defp gen_callback(friend_id) do
    fn %Pigeon.FCM.Notification{response: response, error: error, target: {:token, token}} =
         notification ->
      cond do
        response == :success ->
          Logger.debug("Push successful")

        response == :not_started ->
          Logger.warning("Push notification was sent, but FCM service wasn't started")

        error != nil ->
          %{"details" => [%{"errorCode" => code}]} = error

          case code do
            "UNREGISTERED" ->
              Logger.debug("Token for #{friend_id} looks stale (UNREGISTERED), removing")
              NotificationTokens.invalidate_notification_token({:system, friend_id}, token)

            other ->
              Logger.error(
                "Push notification error (deep) (#{friend_id}): #{inspect(other)} | #{inspect(notification)}"
              )
          end

        true ->
          Logger.error(
            "Push notification error (#{friend_id}): :#{inspect(response)} | #{inspect(notification)}"
          )
      end
    end
  end

  defp gen_batch_notifications(user_id, alter_ids) do
    user = Accounts.get_user!({:system, user_id})

    username = then(user, fn user -> user.username || user.id end)

    image =
      case user.avatar_url do
        nil -> nil
        "" -> nil
        url -> url
      end

    title = "Front update: " <> username

    alters_query =
      from(
        a in Alter,
        where: a.user_id == ^user_id and a.id in ^alter_ids,
        select: a
      )

    friends_query =
      from(
        f in Friendship,
        where: f.user_id == ^user_id,
        # Get notification tokens
        join: n in NotificationToken,
        on: n.user_id == f.friend_id,
        select: {f.friend_id, f.level, n}
      )

    alters = Repo.all(alters_query)

    data =
      Repo.all(friends_query)
      |> Enum.group_by(&elem(&1, 0))
      |> Stream.map(fn {id, list} ->
        {
          id,
          Enum.reduce(list, [], fn {_, _, token}, acc ->
            [token.token | acc]
          end),
          hd(list) |> elem(1)
        }
      end)
      |> Stream.map(fn {id, tokens, level} ->
        visible_alters =
          alters
          |> Stream.filter(fn alter ->
            Alters.can_view_entity?(level, alter.security_level)
          end)
          |> Enum.map_join(", ", & &1.name)
          |> then(fn alters ->
            case alters do
              "" ->
                "No one is fronting"

              alters ->
                case String.length(alters) do
                  length when length > @size_limit ->
                    alters
                    |> String.slice(0..(@size_limit - 3))
                    |> Kernel.<>("\n...")

                  _ ->
                    alters
                end
            end
          end)

        {tokens, {id, visible_alters}}
      end)
      |> Enum.into(%{})

    {title, image, data}
  end
end
